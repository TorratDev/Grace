namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks
open Types
open Events.Reference
open Grace.Shared.Validation.Errors.Reference
open Grace.Shared.Constants
open Commands.Reference

module Reference =

    let GetActorId (referenceId: ReferenceId) = ActorId($"{referenceId}")

    type ReferenceActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Reference
        let mutable actorStartTime = Instant.MinValue
        let log = loggerFactory.CreateLogger("Reference.Actor")
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let eventsStateName = StateName.Reference
        let mutable referenceDto = ReferenceDto.Default
        let referenceEvents = List<ReferenceEvent>()

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto referenceEventType currentReferenceDto =
            let newReferenceDto =
                match referenceEventType with
                | Created(referenceId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText) ->
                    { currentReferenceDto with
                        ReferenceId = referenceId
                        RepositoryId = repositoryId
                        BranchId = branchId
                        DirectoryId = directoryId
                        Sha256Hash = sha256Hash
                        ReferenceType = referenceType
                        ReferenceText = referenceText }
                | LogicalDeleted(force, deleteReason) -> {currentReferenceDto with DeletedAt = Some(getCurrentInstant()); DeleteReason = deleteReason}
                | PhysicalDeleted -> currentReferenceDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentReferenceDto with DeletedAt = None; DeleteReason = String.Empty}

            {newReferenceDto with UpdatedAt = Some(getCurrentInstant())}

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                let mutable message = String.Empty
                let! retrievedEvents = Storage.RetrieveState<List<ReferenceEvent>> stateManager eventsStateName

                match retrievedEvents with
                | Some retrievedEvents ->
                    referenceEvents.AddRange(retrievedEvents)

                    // Apply all events to the state.
                    referenceDto <- retrievedEvents |> Seq.fold (fun referenceDto referenceEvent -> referenceDto |> updateDto referenceEvent.Event) ReferenceDto.Default

                    message <- "Retrieved from database"
                | None -> message <- "Not found in database"

                let duration_ms = getPaddedDuration_ms activateStartTime

                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; Activated {ActorType} {ActorId}. {message}.",
                    getCurrentInstantExtended (),
                    Environment.MachineName,
                    duration_ms,
                    actorName,
                    host.Id,
                    message
                )
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName} ReferenceId: {Id}.",
                getCurrentInstantExtended (),
                actorName,
                context.MethodName,
                this.Id
            )

            // This checks if the actor is still active, but in an undefined state, which will _almost_ never happen.
            // isDisposed is set when the actor is deleted, or if an error occurs where we're not sure of the state and want to reload from the database.
            if isDisposed then
                this.OnActivateAsync().Wait()
                isDisposed <- false

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}.",
                    getCurrentInstantExtended (),
                    Environment.MachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    referenceDto.RepositoryId,
                    referenceDto.BranchId,
                    this.Id
                )
            else
                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}.",
                    getCurrentInstantExtended (),
                    Environment.MachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    currentCommand,
                    referenceDto.RepositoryId,
                    referenceDto.BranchId,
                    this.Id
                )

            logScope.Dispose()
            Task.CompletedTask

        member private this.ApplyEventOld referenceEvent =
            let stateManager = this.StateManager

            task {
                try
                    //if referenceEvents.Count = 0 then do! this.OnFirstWrite()

                    // Add the event to the branchEvents list, and save it to actor state.
                    referenceEvents.Add(referenceEvent)
                    do! Storage.SaveState stateManager eventsStateName referenceEvents

                    // Update the referenceDto with the event.
                    referenceDto <- referenceDto |> updateDto referenceEvent.Event

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.ReferenceEvent referenceEvent
                    let message = serialize graceEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Reference command succeeded." referenceEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                        .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                        .enhance(nameof (ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                        .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex

                    let graceError = GraceError.Create (ReferenceError.getErrorMessage FailedWhileApplyingEvent) referenceEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                        .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                        .enhance(nameof (ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                        .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")
                    |> ignore

                    return Error graceError
            }

            member private this.SchedulePhysicalDeletion(deleteReason, delay, correlationId) =
                let tuple = (referenceDto.RepositoryId, referenceDto.BranchId, referenceDto.ReferenceId, deleteReason, correlationId)

                // There's no good way to do this asynchronously, so we'll just block. Hopefully the Dapr SDK fixes this.
                this
                    .RegisterReminderAsync(ReminderType.PhysicalDeletion, toByteArray tuple, delay, TimeSpan.FromMilliseconds(-1))
                    .Result
                |> ignore

        interface IRemindable with
            override this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                let stateManager = this.StateManager

                match reminderName with
                | ReminderType.Maintenance ->
                    task {
                        // Do some maintenance
                        ()
                    }
                    :> Task
                | ReminderType.PhysicalDeletion ->
                    task {
                        // Get values from state.
                        let (repositoryId, branchId, directoryId, sha256Hash, deleteReason, correlationId) =
                            fromByteArray<RepositoryId * BranchId * DirectoryVersionId * Sha256Hash * DeleteReason * CorrelationId> state

                        this.correlationId <- correlationId

                        // Delete the references for this branch.


                        // Delete saved state for this actor.
                        let! deletedEventsState = Storage.DeleteState stateManager eventsStateName

                        log.LogInformation(
                            "{currentInstant}: CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryId: {DirectoryId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            repositoryId,
                            branchId,
                            this.Id,
                            directoryId,
                            deleteReason
                        )

                        // Set all values to default.
                        referenceDto <- ReferenceDto.Default
                        referenceEvents.Clear()

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true
                    }
                    :> Task
                | _ -> failwith "Unknown reminder type."

        member private this.ApplyEvent (referenceEvent: ReferenceEvent) =
            let stateManager = this.StateManager

            task {
                try
                    //if referenceEvents.Count = 0 then do! this.OnFirstWrite()

                    // Add the event to the referenceEvents list, and save it to actor state.
                    referenceEvents.Add(referenceEvent)
                    do! Storage.SaveState stateManager eventsStateName referenceEvents

                    // Update the referenceDto with the event.
                    referenceDto <- referenceDto |> updateDto referenceEvent.Event

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.ReferenceEvent referenceEvent
                    let message = serialize graceEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    // If this is a Save or Checkpoint reference, schedule a physical deletion based on the default delays from the repository.
                    match referenceEvent.Event with
                    | Created(_, _, _, _, _, referenceType, _) ->
                        do!
                            match referenceType with
                            | ReferenceType.Save ->
                                task {
                                    let repositoryActorProxy =
                                        actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId($"{referenceDto.RepositoryId}"), ActorName.Repository)

                                    let! repositoryDto = repositoryActorProxy.Get(referenceEvent.Metadata.CorrelationId)

                                    this.SchedulePhysicalDeletion(
                                        $"Deletion for saves of {repositoryDto.SaveDays} days.",
                                        TimeSpan.FromDays(repositoryDto.SaveDays),
                                        referenceEvent.Metadata.CorrelationId
                                    )
                                }
                            | ReferenceType.Checkpoint ->
                                task {
                                    let repositoryActorProxy =
                                        actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId($"{referenceDto.RepositoryId}"), ActorName.Repository)

                                    let! repositoryDto = repositoryActorProxy.Get(referenceEvent.Metadata.CorrelationId)

                                    this.SchedulePhysicalDeletion(
                                        $"Deletion for checkpoints of {repositoryDto.CheckpointDays} days.",
                                        TimeSpan.FromDays(repositoryDto.CheckpointDays),
                                        referenceEvent.Metadata.CorrelationId
                                    )
                                }
                            | _ -> () |> returnTask
                            :> Task
                    | _ -> ()

                    let returnValue = GraceReturnValue.Create "Reference command succeeded." referenceEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof(RepositoryId), $"{referenceDto.RepositoryId}")
                        .enhance(nameof(BranchId), $"{referenceDto.BranchId}")
                        .enhance(nameof(ReferenceId), $"{referenceDto.ReferenceId}")
                        .enhance(nameof(DirectoryVersionId), $"{referenceDto.DirectoryId}")
                        .enhance(nameof(ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                        .enhance (nameof(ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex

                    let graceError = GraceError.Create (ReferenceError.getErrorMessage FailedWhileApplyingEvent) referenceEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof(RepositoryId), $"{referenceDto.RepositoryId}")
                        .enhance(nameof(BranchId), $"{referenceDto.BranchId}")
                        .enhance(nameof(ReferenceId), $"{referenceDto.ReferenceId}")
                        .enhance(nameof(DirectoryVersionId), $"{referenceDto.DirectoryId}")
                        .enhance(nameof(ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                        .enhance (nameof(ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")
                    |> ignore

                    return Error graceError
            }

        member private this.SchedulePhysicalDeletion(deleteReason: DeleteReason, delay, correlationId: CorrelationId) =
            let tuple = (referenceDto.RepositoryId, referenceDto.BranchId, referenceDto.DirectoryId, referenceDto.Sha256Hash, deleteReason, correlationId)

            // There's no good way to do this asynchronously, so we'll just block. Hopefully the Dapr SDK fixes this.
            this
                .RegisterReminderAsync(ReminderType.PhysicalDeletion, toByteArray tuple, delay, TimeSpan.FromMilliseconds(-1))
                .Result
            |> ignore

        interface IReferenceActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                not <| referenceDto.ReferenceId.Equals(ReferenceDto.Default.ReferenceId) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                referenceDto |> returnTask

            member this.GetReferenceType correlationId =
                this.correlationId <- correlationId
                referenceDto.ReferenceType |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                referenceDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid (command: ReferenceCommand) (metadata: EventMetadata) =
                    task {
                        if referenceEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error (GraceError.Create (ReferenceError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create(referenceId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText) ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Error (GraceError.Create (ReferenceError.getErrorMessage ReferenceAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error (GraceError.Create (ReferenceError.getErrorMessage ReferenceIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: ReferenceCommand) (metadata: EventMetadata) =
                    task {
                        let! referenceEventType =
                            task {
                                match command with
                                | Create(referenceId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText) ->
                                    return Created(referenceId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText)
                                | DeleteLogical(force, deleteReason) ->
                                    let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId($"{referenceDto.RepositoryId}"), ActorName.Repository)
                                    let! repositoryDto = repositoryActorProxy.Get(metadata.CorrelationId)
                                    this.SchedulePhysicalDeletion(deleteReason, TimeSpan.FromDays(repositoryDto.LogicalDeleteDays), metadata.CorrelationId)
                                    return LogicalDeleted(force, deleteReason)
                                | DeletePhysical ->
                                    isDisposed <- true
                                    return PhysicalDeleted
                                | Undelete -> return Undeleted
                            }

                        let referenceEvent = { Event = referenceEventType; Metadata = metadata }
                        let! returnValue = this.ApplyEvent referenceEvent

                        return returnValue
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
