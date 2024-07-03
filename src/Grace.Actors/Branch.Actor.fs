namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands
open Grace.Actors.Commands.Branch
open Grace.Actors.Constants
open Grace.Actors.Events.Branch
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Branch
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Runtime.Serialization
open System.Threading.Tasks
open NodaTime
open System.Text.Json
open System.Net.Http.Json
open Grace.Shared.Dto.Reference
open Grace.Shared.Services

module Branch =

    let GetActorId (branchId: BranchId) = ActorId($"{branchId}")

    // Branch should support logical deletes with physical deletes set using a Dapr Timer based on a repository-level setting.
    // Branch Deletion should enumerate and delete each reference in the branch.

    type BranchActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Branch
        let log = loggerFactory.CreateLogger("Branch.Actor")
        let eventsStateName = StateName.Branch

        let mutable branchDto: BranchDto = BranchDto.Default
        let branchEvents = List<BranchEvent>()

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto branchEventType currentBranchDto =
            let newBranchDto =
                match branchEventType with
                | Created(branchId, branchName, parentBranchId, basedOn, repositoryId, initialPermissions) ->
                    let mutable branchDto =
                        { BranchDto.Default with
                            BranchId = branchId
                            BranchName = branchName
                            ParentBranchId = parentBranchId
                            BasedOn = basedOn
                            RepositoryId = repositoryId }

                    for referenceType in initialPermissions do
                        branchDto <-
                            match referenceType with
                            | Promotion -> { branchDto with PromotionEnabled = true }
                            | Commit -> { branchDto with CommitEnabled = true }
                            | Checkpoint -> { branchDto with CheckpointEnabled = true }
                            | Save -> { branchDto with SaveEnabled = true }
                            | Tag -> { branchDto with TagEnabled = true }
                            | External -> { branchDto with ExternalEnabled = true }
                            | Rebase -> branchDto // Rebase is always allowed.

                    branchDto
                | Rebased referenceId -> { currentBranchDto with BasedOn = referenceId }
                | NameSet branchName -> { currentBranchDto with BranchName = branchName }
                | Assigned(referenceId, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestPromotion = referenceId; BasedOn = referenceId }
                | Promoted(referenceId, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestPromotion = referenceId; BasedOn = referenceId }
                | Committed(referenceId, directoryVersion, sha256Hash, referenceText) -> { currentBranchDto with LatestCommit = referenceId }
                | Checkpointed(referenceId, directoryVersion, sha256Hash, referenceText) -> { currentBranchDto with LatestCheckpoint = referenceId }
                | Saved(referenceId, directoryVersion, sha256Hash, referenceText) -> { currentBranchDto with LatestSave = referenceId }
                | Tagged(referenceId, directoryVersion, sha256Hash, referenceText) -> currentBranchDto // No changes to currentBranchDto.
                | ExternalCreated(referenceId, directoryVersion, sha256Hash, referenceText) -> currentBranchDto // No changes to currentBranchDto.
                | EnabledAssign enabled -> { currentBranchDto with AssignEnabled = enabled }
                | EnabledPromotion enabled -> { currentBranchDto with PromotionEnabled = enabled }
                | EnabledCommit enabled -> { currentBranchDto with CommitEnabled = enabled }
                | EnabledCheckpoint enabled -> { currentBranchDto with CheckpointEnabled = enabled }
                | EnabledSave enabled -> { currentBranchDto with SaveEnabled = enabled }
                | EnabledTag enabled -> { currentBranchDto with TagEnabled = enabled }
                | EnabledExternal enabled -> { currentBranchDto with ExternalEnabled = enabled }
                | EnabledAutoRebase enabled -> { currentBranchDto with AutoRebaseEnabled = enabled }
                | ReferenceRemoved _ -> currentBranchDto
                | LogicalDeleted(force, deleteReason) -> { currentBranchDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                | PhysicalDeleted -> currentBranchDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentBranchDto with DeletedAt = None; DeleteReason = String.Empty }

            { newBranchDto with UpdatedAt = Some(getCurrentInstant ()) }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                try
                    let mutable message = String.Empty
                    let! retrievedEvents = Storage.RetrieveState<List<BranchEvent>> stateManager eventsStateName

                    match retrievedEvents with
                    | Some retrievedEvents ->
                        // Load the branchEvents from the retrieved events.
                        branchEvents.AddRange(retrievedEvents)

                        // Apply all events to the branchDto.
                        branchDto <-
                            retrievedEvents
                            |> Seq.fold (fun branchDto branchEvent -> branchDto |> updateDto branchEvent.Event) BranchDto.Default

                        // Get the latest references and update the dto.
                        let referenceTypes = [| Save; Checkpoint; Commit; Promotion |]
                        //logToConsole $"In Branch.Actor.OnActivateAsync: About to call getLatestReferenceByReferenceTypes()."
                        let! latestReferences = getLatestReferenceByReferenceTypes referenceTypes branchDto.BranchId
                        latestReferences |> Seq.iter (fun kvp ->
                            let referenceId = kvp.Value.ReferenceId
                            match kvp.Key with
                            | Save -> branchDto <- { branchDto with LatestSave = referenceId }
                            | Checkpoint -> branchDto <- { branchDto with LatestCheckpoint = referenceId }
                            | Commit -> branchDto <- { branchDto with LatestCommit = referenceId }
                            | Promotion -> branchDto <- { branchDto with LatestPromotion = referenceId }
                            | Rebase
                            | External
                            | Tag -> ()
                        )

                        message <- "Retrieved from database"
                    | None -> message <- "Not found in database"

                    let duration_ms = getPaddedDuration_ms activateStartTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; Activated {ActorType} {ActorId}. BranchName: {BranchName}; {message}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        actorName,
                        host.Id,
                        branchDto.BranchName,
                        message
                    )
                with ex ->
                    log.LogError(ex, "Error in Branch.Actor.OnActivateAsync. BranchId: {BranchId}.", host.Id)
            }
            :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync("MaintenanceReminder", Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() = this.UnregisterReminderAsync("MaintenanceReminder")

        member private this.OnFirstWrite() =
            task {
                //let! _ = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName} BranchId: {Id}.",
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
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchId: {Id}; BranchName: {BranchName}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    branchDto.RepositoryId,
                    this.Id,
                    branchDto.BranchName
                )
            else
                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; RepositoryId: {RepositoryId}; BranchId: {Id}; BranchName: {BranchName}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    currentCommand,
                    branchDto.RepositoryId,
                    this.Id,
                    branchDto.BranchName
                )

            logScope.Dispose()
            Task.CompletedTask

        member private this.ApplyEvent branchEvent =
            let stateManager = this.StateManager

            task {
                try
                    if branchEvents.Count = 0 then do! this.OnFirstWrite()

                    // Update the branchDto with the event.
                    branchDto <- branchDto |> updateDto branchEvent.Event

                    match branchEvent.Event with
                    | Assigned (referenceId, _, _, _)
                    | Promoted (referenceId, _, _, _)
                    | Committed (referenceId, _, _, _)
                    | Checkpointed (referenceId, _, _, _)
                    | Saved (referenceId, _, _, _)
                    | Tagged (referenceId, _, _, _)
                    | ExternalCreated (referenceId, _, _, _) ->
                        // Don't save these reference creation events, and don't send them as events; that was done by the Reference actor when the reference was created.
                        branchEvent.Metadata.Properties.Add(nameof (ReferenceId), $"{referenceId}")
                    | Rebased (referenceId) ->
                        branchEvent.Metadata.Properties.Add(nameof (ReferenceId), $"{referenceId}")
                    | _ ->
                        // For all other events, add the event to the branchEvents list, and save it to actor state.
                        branchEvents.Add(branchEvent)
                        do! Storage.SaveState stateManager eventsStateName branchEvents

                        // Publish the event to the rest of the world.
                        let graceEvent = Events.GraceEvent.BranchEvent branchEvent
                        let message = serialize graceEvent
                        do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Branch command succeeded." branchEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof (RepositoryId), $"{branchDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{branchDto.BranchId}")
                        .enhance(nameof (BranchName), $"{branchDto.BranchName}")
                        .enhance(nameof (ParentBranchId), $"{branchDto.ParentBranchId}")
                        .enhance (nameof (BranchEventType), $"{getDiscriminatedUnionFullName branchEvent.Event}")
                    |> ignore

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof (ReferenceId)) then
                        returnValue.Properties.Add(nameof (ReferenceId), branchEvent.Metadata.Properties[nameof (ReferenceId)])

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex
                    let graceError = GraceError.Create (BranchError.getErrorMessage FailedWhileApplyingEvent) branchEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (RepositoryId), $"{branchDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{branchDto.BranchId}")
                        .enhance(nameof (BranchName), $"{branchDto.BranchName}")
                        .enhance(nameof (ParentBranchId), $"{branchDto.ParentBranchId}")
                        .enhance (nameof (BranchEventType), $"{getDiscriminatedUnionFullName branchEvent.Event}")
                    |> ignore

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof (ReferenceId)) then
                        graceError.enhance(nameof (ReferenceId), branchEvent.Metadata.Properties[nameof (ReferenceId)]) |> ignore

                    return Error graceError
            }

        member private this.SchedulePhysicalDeletion(deleteReason, delay, correlationId) =
            let tuple = (branchDto.RepositoryId, branchDto.BranchId, branchDto.BranchName, branchDto.ParentBranchId, deleteReason, correlationId)

            // There's no good way to do this asynchronously, so we'll just block. Hopefully the Dapr SDK fixes this.
            this
                .RegisterReminderAsync(ReminderType.PhysicalDeletion, toByteArray tuple, delay, TimeSpan.FromMilliseconds(-1))
                .Result
            |> ignore

        interface IBranchActor with

            member this.GetEvents correlationId =
                task {
                    this.correlationId <- correlationId
                    return branchEvents :> IReadOnlyList<BranchEvent>
                }

            member this.Exists correlationId =
                this.correlationId <- correlationId
                branchDto.UpdatedAt.IsSome |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                branchDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        if
                            branchEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)
                            && (branchEvents.Count > 3)
                        then
                            return Error(GraceError.Create (BranchError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | BranchCommand.Create(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions) ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (BranchError.getErrorMessage BranchAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) metadata.CorrelationId)
                    }

                let addReference directoryId sha256Hash referenceText referenceType =
                    task {
                        let referenceId: ReferenceId = ReferenceId.NewGuid()
                        let actorId = Reference.GetActorId referenceId

                        let referenceActor = actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, Constants.ActorName.Reference)

                        let referenceDto = {ReferenceDto.Default with ReferenceId = referenceId; RepositoryId = branchDto.RepositoryId; BranchId = branchDto.BranchId; DirectoryId = directoryId; Sha256Hash = sha256Hash; ReferenceText = referenceText; ReferenceType = referenceType}
                        let referenceCommand = Reference.ReferenceCommand.Create referenceDto
                        match! referenceActor.Handle referenceCommand metadata with
                        | Ok _ -> return Ok referenceId
                        | Error error -> return Error error
                    }

                let processCommand (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions) ->
                                        use newCacheEntry =
                                            memoryCache.CreateEntry(
                                                $"BrN:{branchName}",
                                                Value = branchId,
                                                AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                            )

                                        return Ok (Created(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions))
                                    | BranchCommand.Rebase referenceId ->
                                        metadata.Properties.Add("BasedOn", $"{referenceId}")
                                        metadata.Properties.Add(nameof (ReferenceId), $"{referenceId}")
                                        metadata.Properties.Add(nameof (BranchId), $"{this.Id}")
                                        metadata.Properties.Add(nameof (BranchName), $"{branchDto.BranchName}")
                                        let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(Reference.GetActorId referenceId, ActorName.Reference)
                                        let! promotionDto = referenceActorProxy.Get metadata.CorrelationId
                                        match! addReference promotionDto.DirectoryId promotionDto.Sha256Hash promotionDto.ReferenceText ReferenceType.Rebase with
                                        | Ok referenceId -> return Ok (Rebased referenceId)
                                        | Error error -> return Error error
                                    | SetName branchName -> return Ok (NameSet branchName)
                                    | EnableAssign enabled -> return Ok (EnabledAssign enabled)
                                    | EnablePromotion enabled -> return Ok (EnabledPromotion enabled)
                                    | EnableCommit enabled -> return Ok (EnabledCommit enabled)
                                    | EnableCheckpoint enabled -> return Ok (EnabledCheckpoint enabled)
                                    | EnableSave enabled -> return Ok (EnabledSave enabled)
                                    | EnableTag enabled -> return Ok (EnabledTag enabled)
                                    | EnableExternal enabled -> return Ok (EnabledExternal enabled)
                                    | EnableAutoRebase enabled -> return Ok (EnabledAutoRebase enabled)
                                    | BranchCommand.Assign(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.Promotion with
                                        | Ok referenceId -> return Ok (Assigned(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Promote(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.Promotion with
                                        | Ok referenceId -> return Ok (Promoted(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Commit(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.Commit with
                                        | Ok referenceId -> return Ok (Committed(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Checkpoint(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.Checkpoint with
                                        | Ok referenceId -> return Ok (Checkpointed(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Save(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.Save with
                                        | Ok referenceId -> return Ok (Saved(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Tag(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.Tag with
                                        | Ok referenceId -> return Ok (Tagged(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.CreateExternal(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReference directoryVersionId sha256Hash referenceText ReferenceType.External with
                                        | Ok referenceId -> return Ok (ExternalCreated(referenceId, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | RemoveReference referenceId -> return Ok (ReferenceRemoved referenceId)
                                    | DeleteLogical(force, deleteReason) ->
                                        let repositoryActorProxy =
                                            actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId($"{branchDto.RepositoryId}"), ActorName.Repository)
                                        let! repositoryDto = repositoryActorProxy.Get(metadata.CorrelationId)
                                        this.SchedulePhysicalDeletion(deleteReason, TimeSpan.FromDays(repositoryDto.LogicalDeleteDays), metadata.CorrelationId)
                                        return Ok (LogicalDeleted(force, deleteReason))
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return Ok PhysicalDeleted
                                    | Undelete -> return Ok Undeleted
                                }

                            match event with
                            | Ok event -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.Create $"{createExceptionResponse ex}" metadata.CorrelationId)
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }

            member this.Get correlationId =
                this.correlationId <- correlationId
                branchDto |> returnTask

            member this.GetParentBranch correlationId =
                task {
                    this.correlationId <- correlationId
                    let actorId = ActorId($"{branchDto.ParentBranchId}")

                    let branchActorProxy = this.Host.ProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)

                    return! branchActorProxy.Get correlationId
                }

            member this.GetLatestCommit correlationId =
                this.correlationId <- correlationId
                branchDto.LatestCommit |> returnTask

            member this.GetLatestPromotion correlationId =
                this.correlationId <- correlationId
                branchDto.LatestPromotion |> returnTask

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
                        let (repositoryId, branchId, branchName, parentBranchId, deleteReason, correlationId) =
                            fromByteArray<string * string * string * string * string * string> state

                        this.correlationId <- correlationId

                        // Delete the references for this branch.


                        // Delete saved state for this actor.
                        let! deletedEventsState = Storage.DeleteState stateManager eventsStateName

                        log.LogInformation(
                            "{currentInstant}: CorrelationId: {correlationId}; Deleted physical state for branch; RepositoryId: {repositoryId}; BranchId: {branchId}; BranchName: {branchName}; ParentBranchId: {parentBranchId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            repositoryId,
                            branchId,
                            branchName,
                            parentBranchId,
                            deleteReason
                        )

                        // Set all values to default.
                        branchDto <- BranchDto.Default
                        branchEvents.Clear()

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true
                    }
                    :> Task
                | _ -> failwith "Unknown reminder type."
