﻿namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module RepositoryName =

    let actorName = ActorName.RepositoryName
    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId (repositoryName: string) = ActorId(repositoryName)

    type RepositoryNameActor(host: ActorHost) =
        inherit Actor(host)

        let idSections = host.Id.GetId().Split('|')
        let repositoryName = idSections[0]
        let ownerId = idSections[1]
        let organizationId = idSections[2]

        let log = loggerFactory.CreateLogger("RepositoryName.Actor")

        let mutable cachedRepositoryId: string option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")

            log.LogInformation(
                "{CurrentInstant}: CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}; RepositoryName: {RepositoryName}; RepositoryId: {RepositoryId}; Duration: {duration_ms}ms.",
                getCurrentInstantExtended (),
                this.correlationId,
                actorName,
                context.MethodName,
                ownerId,
                organizationId,
                repositoryName,
                (if Option.isSome cachedRepositoryId then cachedRepositoryId.Value else "None"),
                duration_ms
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IRepositoryNameActor with
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                cachedRepositoryId |> returnTask

            member this.SetRepositoryId (repositoryId: string) correlationId =
                this.correlationId <- correlationId
                let mutable guid = Guid.Empty

                if Guid.TryParse(repositoryId, &guid) && guid <> Guid.Empty then
                    cachedRepositoryId <- Some repositoryId

                Task.CompletedTask
