namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open FSharpPlus
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Branch
open Grace.Actors.Commands.Repository
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Repository
open Grace.Shared.Validation
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open NodaTime
open OpenTelemetry.Trace
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Linq
open System.Threading.Tasks
open System.Text
open System.Text.Json

module Repository =

    type Validations<'T when 'T :> RepositoryParameters> = 'T -> ValueTask<Result<unit, RepositoryError>> array

    let activitySource = new ActivitySource("Repository")

    let log = ApplicationContext.loggerFactory.CreateLogger("Repository.Server")

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy (repositoryId: string) =
        let actorId = ActorId(repositoryId)
        actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)

    let processCommand<'T when 'T :> RepositoryParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<RepositoryCommand>) =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let commandName = context.Items["Command"] :?> string
                let graceIds = context.Items["GraceIds"] :?> GraceIds
                let! parameters = context |> parse<'T>

                let handleCommand repositoryId cmd =
                    task {
                        let actorProxy = getActorProxy repositoryId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            let graceIds = getGraceIds context
                            graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                            graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                            graceReturnValue.Properties[nameof (RepositoryId)] <- graceIds.RepositoryId

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            log.LogDebug(
                                "{currentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return!
                                context
                                |> result400BadRequest { graceError with Properties = getParametersAsDictionary parameters }
                    }

                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{currentInstant}: In Repository.Server.processCommand: RepositoryId: {repositoryId}; validationsPassed: {validationsPassed}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    graceIds.RepositoryId,
                    validationsPassed,
                    (getCorrelationId context)
                )

                if validationsPassed then
                    let! cmd = command parameters

                    let! repositoryId =
                        resolveRepositoryId
                            parameters.OwnerId
                            parameters.OwnerName
                            parameters.OrganizationId
                            parameters.OrganizationName
                            parameters.RepositoryId
                            parameters.RepositoryName
                            parameters.CorrelationId

                    match repositoryId, commandName = nameof (Create) with
                    | Some repositoryId, _ ->
                        // If Id is Some, then we know we have a valid Id.
                        if String.IsNullOrEmpty(parameters.RepositoryId) then
                            parameters.RepositoryId <- repositoryId

                        return! handleCommand repositoryId cmd
                    | None, true ->
                        // If it's None, but this is a Create command, still valid, just use the Id from the parameters.
                        return! handleCommand parameters.RepositoryId cmd
                    | None, false ->
                        // If it's None, and this is not a Create command, then we have a bad request.
                        log.LogDebug(
                            "{currentInstant}: In Repository.Server.processCommand: resolveRepositoryId failed. Repository does not exist. repositoryId: {repositoryId}; repositoryName: {repositoryName}; Path: {path}; CorrelationId: {correlationId}.",
                            getCurrentInstantExtended (),
                            parameters.RepositoryId,
                            parameters.RepositoryName,
                            context.Request.Path,
                            (getCorrelationId context)
                        )

                        return!
                            context
                            |> result400BadRequest (
                                GraceError.CreateWithMetadata
                                    (RepositoryError.getErrorMessage RepositoryDoesNotExist)
                                    (getCorrelationId context)
                                    (getParametersAsDictionary parameters)
                            )
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = RepositoryError.getErrorMessage error
                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError = GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getParametersAsDictionary parameters)

                    graceError.Properties.Add("Path", context.Request.Path)
                    graceError.Properties.Add("Error", errorMessage)
                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{currentInstant}: Exception in Repository.Server.processCommand; Path: {path}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    context.Request.Path,
                    (getCorrelationId context)
                )

                return!
                    context
                    |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    let processQuery<'T, 'U when 'T :> RepositoryParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (maxCount: int)
        (query: QueryResult<IRepositoryActor, 'U>)
        =
        task {
            try
                use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
                let graceIds = getGraceIds context
                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    // Get the actor proxy for the repository.
                    let actorProxy = getActorProxy graceIds.RepositoryId

                    // Execute the query.
                    let! queryResult = query context maxCount actorProxy

                    // Wrap the query result in a GraceReturnValue.
                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult (getCorrelationId context))
                            .enhance(nameof(OwnerId), graceIds.OwnerId)
                            .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                            .enhance(nameof(RepositoryId), graceIds.RepositoryId)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (RepositoryError.getErrorMessage error) (getCorrelationId context))
                            .enhance(nameof(OwnerId), graceIds.OwnerId)
                            .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                            .enhance(nameof(RepositoryId), graceIds.RepositoryId)

                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{currentInstant}: Exception in Repository.Server.processQuery; Path: {path}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    context.Request.Path,
                    (getCorrelationId context)
                )

                return!
                    context
                    |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Create a new repository.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                //let! parameters = context |> parse<CreateParameters>
                //logToConsole $"parameters.ObjectStorageProvider: {parameters.ObjectStorageProvider}"
                let validations (parameters: CreateRepositoryParameters) =
                    [| Repository.repositoryIdDoesNotExist parameters.RepositoryId parameters.CorrelationId RepositoryIdAlreadyExists
                       Repository.repositoryNameIsUnique
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryNameAlreadyExists |]

                let command (parameters: CreateRepositoryParameters) =
                    task {
                        let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName parameters.CorrelationId

                        let! organizationId =
                            resolveOrganizationId
                                ownerId.Value
                                parameters.OwnerName
                                parameters.OrganizationId
                                parameters.OrganizationName
                                parameters.CorrelationId

                        return
                            Create(
                                RepositoryName parameters.RepositoryName,
                                (Guid.Parse(parameters.RepositoryId)),
                                (Guid.Parse(ownerId.Value)),
                                (Guid.Parse(organizationId.Value))
                            )
                    }
                    |> ValueTask<RepositoryCommand>

                context.Items.Add("Command", nameof (Create))
                return! processCommand context validations command
            }

    /// Sets the search visibility of the repository.
    let SetVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryVisibilityParameters) =
                    [| Repository.visibilityIsValid parameters.Visibility InvalidVisibilityValue
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetRepositoryVisibilityParameters) =
                    SetVisibility(discriminatedUnionFromString<RepositoryVisibility>(parameters.Visibility).Value)
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetVisibility))
                return! processCommand context validations command
            }

    /// Sets the number of days to keep saves in the repository.
    let SetSaveDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetSaveDaysParameters) =
                    [| Repository.daysIsValid parameters.SaveDays InvalidSaveDaysValue
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetSaveDaysParameters) = SetSaveDays(parameters.SaveDays) |> returnValueTask

                context.Items.Add("Command", nameof (SetSaveDays))
                return! processCommand context validations command
            }

    /// Sets the number of days to keep checkpoints in the repository.
    let SetCheckpointDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetCheckpointDaysParameters) =
                    [| Repository.daysIsValid parameters.CheckpointDays InvalidCheckpointDaysValue
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetCheckpointDaysParameters) = SetCheckpointDays(parameters.CheckpointDays) |> returnValueTask

                context.Items.Add("Command", nameof (SetCheckpointDays))
                return! processCommand context validations command
            }

    /// Sets the number of days to keep diff contents in the database.
    let SetDiffCacheDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDiffCacheDaysParameters) =
                    [| Repository.daysIsValid parameters.DiffCacheDays InvalidDiffCacheDaysValue
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetDiffCacheDaysParameters) = SetDiffCacheDays(parameters.DiffCacheDays) |> returnValueTask

                context.Items.Add("Command", nameof (SetDiffCacheDays))
                return! processCommand context validations command
            }

    /// Sets the number of days to keep recursive directory version contents in the database.
    let SetDirectoryVersionCacheDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDirectoryVersionCacheDaysParameters) =
                    [| Repository.daysIsValid parameters.DirectoryVersionCacheDays InvalidDirectoryVersionCacheDaysValue
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetDirectoryVersionCacheDaysParameters) =
                    SetDirectoryVersionCacheDays(parameters.DirectoryVersionCacheDays)
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetDirectoryVersionCacheDays))
                return! processCommand context validations command
            }

    /// Sets the status of the repository (Public, Private).
    let SetStatus: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryStatusParameters) =
                    [| DiscriminatedUnion.isMemberOf<RepositoryStatus, RepositoryError> parameters.Status InvalidRepositoryStatus
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetRepositoryStatusParameters) =
                    SetRepositoryStatus(discriminatedUnionFromString<RepositoryStatus>(parameters.Status).Value)
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetRepositoryStatus))
                return! processCommand context validations command
            }

    /// Sets the default server API version for the repository.
    let SetDefaultServerApiVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDefaultServerApiVersionParameters) =
                    [| String.isNotEmpty parameters.DefaultServerApiVersion InvalidServerApiVersion
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetDefaultServerApiVersionParameters) =
                    SetDefaultServerApiVersion(parameters.DefaultServerApiVersion)
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetDefaultServerApiVersion))
                return! processCommand context validations command
            }

    /// Sets whether or not to keep saves in the repository.
    let SetRecordSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RecordSavesParameters) =
                    [| Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: RecordSavesParameters) = SetRecordSaves(parameters.RecordSaves) |> returnValueTask

                context.Items.Add("Command", nameof (SetRecordSaves))
                return! processCommand context validations command
            }

    /// Sets the description of the repository.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryDescriptionParameters) =
                    [| String.isNotEmpty parameters.Description DescriptionIsRequired
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: SetRepositoryDescriptionParameters) = SetDescription(parameters.Description) |> returnValueTask

                context.Items.Add("Command", nameof (SetDescription))
                return! processCommand context validations command
            }

    /// Sets the name of the repository.
    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryNameParameters) =
                    [| String.isNotEmpty parameters.NewName RepositoryNameIsRequired
                       String.isValidGraceName parameters.NewName InvalidRepositoryName
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted
                       Repository.repositoryNameIsUnique
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.NewName
                           parameters.CorrelationId
                           RepositoryNameAlreadyExists |]

                let command (parameters: SetRepositoryNameParameters) = SetName(parameters.NewName) |> returnValueTask

                context.Items.Add("Command", nameof (SetName))
                return! processCommand context validations command
            }

    /// Deletes the repository.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteRepositoryParameters) =
                    [| String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Repository.repositoryIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsDeleted |]

                let command (parameters: DeleteRepositoryParameters) = DeleteLogical(parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof (DeleteLogical))
                return! processCommand context validations command
            }

    /// Undeletes a previously-deleted repository.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RepositoryParameters) =
                    [| Repository.repositoryIsDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryIsNotDeleted |]

                let command (parameters: RepositoryParameters) = Undelete |> returnValueTask

                context.Items.Add("Command", nameof (Undelete))
                return! processCommand context validations command
            }

    /// Checks if a repository exists with the given parameters.
    let Exists: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: RepositoryParameters) = [||]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task { return! actorProxy.Exists(getCorrelationId context) }

                    let! parameters = context |> parse<RepositoryParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Checks if a repository is empty - that is, just created - or not.
    let IsEmpty: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: RepositoryParameters) = [||]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task { return! actorProxy.IsEmpty(getCorrelationId context) }

                    let! parameters = context |> parse<RepositoryParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a repository.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: RepositoryParameters) = [||]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) = task { return! actorProxy.Get(getCorrelationId context) }

                    let! parameters = context |> parse<RepositoryParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a repository's branches.
    let GetBranches: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetBranchesParameters) = [| Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds
                            let repositoryId = Guid.Parse(graceIds.RepositoryId)
                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranches repositoryId maxCount includeDeleted (getCorrelationId context)
                        }

                    let! parameters = context |> parse<GetBranchesParameters>
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)
                    let! result = processQuery context parameters validations 1000 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a list of references, given a list of reference IDs.
    let GetReferencesByReferenceId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesByReferenceIdParameters) =
                        [| Input.listIsNonEmpty parameters.ReferenceIds ReferenceIdsAreRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let referenceIds = context.Items["ReferenceIds"] :?> IEnumerable<ReferenceId>

                            log.LogDebug("In Repository.Server.GetReferencesByReferenceId: ReferenceIds: {referenceIds}", serialize referenceIds)

                            return! getReferencesByReferenceId referenceIds maxCount
                        }

                    let! parameters = context |> parse<GetReferencesByReferenceIdParameters>
                    context.Items.Add("ReferenceIds", parameters.ReferenceIds)
                    let! result = processQuery context parameters validations parameters.MaxCount query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a list of branches, given a list of branch IDs.
    let GetBranchesByBranchId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetBranchesByBranchIdParameters) =
                        [| Input.listIsNonEmpty parameters.BranchIds BranchIdsAreRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let repositoryId = Guid.Parse(graceIds.RepositoryId)
                            let branchIdsFromContext = (context.Items["BranchIds"] :?> string)

                            let branchIds =
                                branchIdsFromContext
                                    .Split(',', StringSplitOptions.TrimEntries)
                                    .Select(fun branchId -> Guid.Parse(branchId))

                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranchesByBranchId repositoryId branchIds maxCount includeDeleted
                        }

                    let! parameters = context |> parse<GetBranchesByBranchIdParameters>

                    let branchIdList = parameters.BranchIds.ToList() // We need .Count below, so may as well materialize it once here.

                    let branchIds = branchIdList.Aggregate(StringBuilder(), (fun state branchId -> state.Append($"{branchId},")))

                    context.Items.Add("BranchIds", (branchIds.ToString())[0..^1])
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)

                    let! result = processQuery context parameters validations (branchIdList.Count) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }
