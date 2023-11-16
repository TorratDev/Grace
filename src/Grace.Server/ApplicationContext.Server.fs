﻿namespace Grace.Server

open Azure.Storage
open Azure.Storage.Blobs
open CosmosJsonSerializer
open Dapr.Client
open Dapr.Actors.Client
open Grace.Actors.Constants
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Linq
open System.Threading.Tasks
open System.Net.Http
open System
open System.Net.Sockets

module ApplicationContext =

    let mutable private configuration: IConfiguration = null
    let Configuration(): IConfiguration = configuration

    let mutable actorProxyFactory: IActorProxyFactory = null
    let mutable actorStateStorageProvider: ActorStateStorageProvider = ActorStateStorageProvider.Unknown
    let mutable loggerFactory: ILoggerFactory = null
    let mutable memoryCache: IMemoryCache = null

    /// <summary>
    /// Sets the Application global configuration.
    /// </summary>
    /// <param name="config">The configuration to set.</param>
    let setConfiguration (config: IConfiguration) =
        logToConsole $"In setConfiguration: isNull(config): {isNull(config)}."
        configuration <- config
        //configuration.AsEnumerable() |> Seq.iter (fun kvp -> logToConsole $"{kvp.Key}: {kvp.Value}")

    let setActorProxyFactory proxyFactory =
        actorProxyFactory <- proxyFactory
        Grace.Actors.Services.setActorProxyFactory proxyFactory

    let setActorStateStorageProvider actorStateStorage =
        actorStateStorageProvider <- actorStateStorage
        Grace.Actors.Services.setActorStateStorageProvider actorStateStorageProvider

    let setLoggerFactory logFactory =
        loggerFactory <- logFactory
        Grace.Actors.Services.setLoggerFactory loggerFactory

    type StorageAccount =
        {
            StorageAccountName: string;
            StorageAccountConnectionString: string;
        }

    let daprHttpEndpoint = $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"
    let daprGrpcEndpoint = $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)}"
    logToConsole $"daprHttpEndpoint: {daprHttpEndpoint}; daprGrpcEndpoint: {daprGrpcEndpoint}"
    let daprClient = DaprClientBuilder().UseJsonSerializationOptions(Constants.JsonSerializerOptions).UseHttpEndpoint(daprHttpEndpoint).UseGrpcEndpoint(daprGrpcEndpoint).Build()
    
    let mutable sharedKeyCredential: StorageSharedKeyCredential = null
    let mutable grpcPortListener: TcpListener = null
    
    let defaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage

    let Set = 
        task {
            let mutable isReady = false

            // Wait for the Dapr gRPC port to be ready.
            let mutable gRPCPort: int = 50001   // This is Dapr's default gRPC port.
            let grpcPortString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)
            Int32.TryParse(grpcPortString, &gRPCPort) |> ignore
            let mutable counter = 0
            while not <| isReady do
                do! Task.Delay(TimeSpan.FromSeconds(2.0))
                logToConsole $"Checking if gRPC port {gRPCPort} is ready."
                let tcpListeners = Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                if tcpListeners.Length > 0 then logToConsole "Active TCP listeners:"
                for t in tcpListeners do
                    logToConsole $"{t.Address}:{t.Port} {t.AddressFamily}."
                if tcpListeners.Any(fun tcpListener -> tcpListener.Port = gRPCPort) then
                    logToConsole $"gRPC port is ready."
                    isReady <- true
                else
                    counter <- counter + 1
                    if counter > 1800 then
                        logToConsole $"gRPC port is not ready after {counter} seconds. Exiting."
                        Environment.Exit(1)
            
            let storageKey = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey)
            sharedKeyCredential <- StorageSharedKeyCredential(DefaultObjectStorageAccount, storageKey)

            let cosmosDbConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureCosmosDBConnectionString)
            let cosmosDatabaseName = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.CosmosDatabaseName)
            let cosmosContainerName = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.CosmosContainerName)

            // Get a reference to the CosmosDB database.
            let cosmosClientOptions = CosmosClientOptions(
                ApplicationName = Constants.GraceServerAppId,
                EnableContentResponseOnWrite = false,
                LimitToEndpoint = true,
                Serializer = new CosmosJsonSerializer(Constants.JsonSerializerOptions))

#if DEBUG
            // The CosmosDB emulator uses a self-signed certificate, and, by default, HttpClient will refuse
            //   to connect over https: if the certificate can't be traced back to a root.
            // These settings allow Grace Server to access the CosmosDB Emulator by bypassing TLS.
            // And none of this matters if Dapr won't bypass TLS as well. 🤷
            //let httpClientFactory = fun () ->
            //    let httpMessageHandler: HttpMessageHandler = new HttpClientHandler(
            //        ServerCertificateCustomValidationCallback = (fun _ _ _ _ -> true))
            //    new HttpClient(httpMessageHandler)
            //cosmosClientOptions.HttpClientFactory <- httpClientFactory
            //cosmosClientOptions.ConnectionMode <- ConnectionMode.Direct
#endif
            let cosmosClient = new CosmosClient(cosmosDbConnectionString, cosmosClientOptions)            
            let! databaseResponse = cosmosClient.CreateDatabaseIfNotExistsAsync(cosmosDatabaseName)
            let database = databaseResponse.Database

            // Get a reference to the CosmosDB container.
            let containerProperties = ContainerProperties(Id = cosmosContainerName, PartitionKeyPath = "/partitionKey", DefaultTimeToLive = 3600)
            let! containerResponse = database.CreateContainerIfNotExistsAsync(containerProperties)
            let cosmosContainer = containerResponse.Container

            // Create a MemoryCache instance.
            memoryCache <- new MemoryCache(MemoryCacheOptions(), loggerFactory)

            // Inject the CosmosClient and CosmosContainer into Actor Services.
            Grace.Actors.Services.setCosmosClient cosmosClient
            Grace.Actors.Services.setCosmosContainer cosmosContainer
            Grace.Actors.Services.setMemoryCache memoryCache
        } :> Task
