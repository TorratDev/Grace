namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open FSharpPlus
open Giraffe
open Grace.Actors
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Shared.Constants
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open Services
open Microsoft.Extensions.Caching.Memory

module Validations =

    let log = ApplicationContext.loggerFactory.CreateLogger("Validations.Server")

    /// Converts an Option to a Result<unit, 'TError> to match the format of validation functions.
    let optionToResult<'TError, 'T> (error: 'TError) (option: Task<'T option>) =
        task {
            match! option with
            | Some value -> return Ok ()
            | None -> return Error error
        }

    module Owner =

        /// Validates that the given ownerId exists in the database.
        let ownerIdExists<'T> (ownerId: string) correlationId (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty

                if (not <| String.IsNullOrEmpty(ownerId)) && Guid.TryParse(ownerId, &ownerGuid) then
                    match memoryCache.GetOwnerIdEntry ownerGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.ExistsValue -> return Ok()
                        | MemoryCache.DoesNotExistValue -> return Error error
                        | _ -> return! ownerExists ownerId correlationId |> optionToResult error
                    | None -> return! ownerExists ownerId correlationId |> optionToResult error
                else
                    return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given ownerId does not already exist in the database.
        let ownerIdDoesNotExist<'T> (ownerId: string) correlationId (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty

                if (not <| String.IsNullOrEmpty(ownerId)) && Guid.TryParse(ownerId, &ownerGuid) then
                    let actorId = Owner.GetActorId(ownerGuid)
                    let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)
                    let! exists = ownerActorProxy.Exists correlationId
                    if exists then return Error error else return Ok()
                else
                    return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the owner exists in the database.
        let ownerExists<'T> ownerId ownerName (context: HttpContext) (error: 'T) =
            let result =
                let graceIds = getGraceIds context
                if graceIds.HasOwner then Ok() else Error error

            ValueTask.FromResult(result)

        /// Validates that the given ownerName does not already exist in the database.
        let ownerNameDoesNotExist<'T> (ownerName: string) correlationId (error: 'T) =
            task {
                let! ownerNameExists = ownerNameExists ownerName false correlationId
                if ownerNameExists then return Error error else return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the owner is deleted.
        let ownerIsDeleted<'T> context correlationId (error: 'T) =
            task {
                let graceIds = getGraceIds context
                let isDeleted = memoryCache.Get<string>($"{graceIds.OwnerId}deleted")
                match isDeleted with
                | MemoryCache.DoesNotExistValue -> return Ok()
                | MemoryCache.ExistsValue -> return Error error
                | _ ->
                    let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(ActorId(graceIds.OwnerId), ActorName.Owner)

                    let! isDeleted = ownerActorProxy.IsDeleted correlationId

                    if isDeleted then
                        use newCacheEntry =
                            memoryCache.CreateEntry($"{graceIds.OwnerId}deleted", Value = MemoryCache.DoesNotExistValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                        return Ok()
                    else
                        use newCacheEntry =
                            memoryCache.CreateEntry($"{graceIds.OwnerId}deleted", Value = MemoryCache.ExistsValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                        return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the owner is not deleted.
        let ownerIsNotDeleted<'T> context correlationId (error: 'T) =
            task {
                match! ownerIsDeleted context correlationId error with
                | Ok _ -> return Error error
                | Error _ -> return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given ownerId does not already exist in the database.
        let ownerDoesNotExist<'T> ownerId ownerName correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(ownerId) && not <| String.IsNullOrEmpty(ownerName) then
                    match! ownerExists ownerId ownerName correlationId error with
                    | Ok _ -> return Error error
                    | Error _ -> return Ok()
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

    module Organization =

        /// Validates that the given organizationId exists in the database.
        let organizationIdExists<'T> (organizationId: string) correlationId (error: 'T) =
            task {
                let mutable organizationGuid = Guid.Empty

                if (not <| String.IsNullOrEmpty(organizationId))
                    && Guid.TryParse(organizationId, &organizationGuid)
                then
                    match memoryCache.GetOrganizationIdEntry organizationGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.ExistsValue -> return Ok()
                        | MemoryCache.DoesNotExistValue -> return Error error
                        | _ -> return! organizationExists organizationId correlationId |> optionToResult error
                    | None -> return! organizationExists organizationId correlationId |> optionToResult error
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given organizationId does not already exist in the database.
        let organizationIdDoesNotExist<'T> (organizationId: string) correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(organizationId) then
                    match! organizationIdExists organizationId correlationId error with
                    | Ok _ -> return Error error
                    | Error _ -> return Ok()
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given organizationName does not already exist for this owner.
        let organizationNameIsUniqueWithinOwner<'T> (ownerId: string) (ownerName: string) (organizationName: string) (context: HttpContext) correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(organizationName) then
                    let graceIds = getGraceIds context

                    match! organizationNameIsUnique graceIds.OwnerId organizationName correlationId with
                    | Ok isUnique ->
                        //logToConsole
                        //    $"In organizationNameIsUnique: correlationId: {correlationId}; ownerId: {ownerId}; ownerName: {ownerName}; organizationName: {organizationName}; isUnique: {isUnique}"

                        if isUnique then return Ok() else return Error error
                    | Error internalError ->
                        logToConsole internalError
                        return Error error
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the organization exists.
        let organizationExists<'T> ownerId ownerName organizationId organizationName correlationId (error: 'T) =
            task {
                try
                    let mutable organizationGuid = Guid.Empty

                    match! resolveOrganizationId ownerId ownerName organizationId organizationName correlationId with
                    | Some organizationId ->
                        if Guid.TryParse(organizationId, &organizationGuid) then
                            let exists = memoryCache.Get<string>(organizationGuid)

                            match exists with
                            | MemoryCache.ExistsValue -> return Ok()
                            | MemoryCache.DoesNotExistValue -> return Error error
                            | _ ->
                                let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(ActorId(organizationId), ActorName.Organization)

                                let! exists = organizationActorProxy.Exists correlationId

                                if exists then
                                    use newCacheEntry =
                                        memoryCache.CreateEntry(organizationGuid, Value = MemoryCache.ExistsValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                                    return Ok()
                                else
                                    return Error error
                        else
                            return Ok()
                    | None -> return Error error
                with ex ->
                    log.LogError(ex, "{currentInstant}: Exception in Grace.Server.Validations.organizationExists.", getCurrentInstantExtended ())

                    return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the organization does not exist.
        let organizationDoesNotExist<'T> ownerId ownerName organizationId organizationName correlationId (error: 'T) =
            task {
                match! organizationExists ownerId ownerName organizationId organizationName correlationId error with
                | Ok _ -> return Error error
                | Error error -> return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the organization is deleted.
        let organizationIsDeleted<'T> context correlationId (error: 'T) =
            task {
                let graceIds = getGraceIds context
                let isDeleted = memoryCache.Get<string>($"{graceIds.OrganizationId}deleted")
                match isDeleted with
                | MemoryCache.DoesNotExistValue -> return Ok()
                | MemoryCache.ExistsValue -> return Error error
                | _ ->
                    let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(ActorId(graceIds.OrganizationId), ActorName.Organization)

                    let! isDeleted = organizationActorProxy.IsDeleted correlationId

                    if isDeleted then
                        use newCacheEntry =
                            memoryCache.CreateEntry(
                                $"{graceIds.OrganizationId}deleted",
                                Value = MemoryCache.DoesNotExistValue,
                                AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            )

                        return Ok()
                    else
                        use newCacheEntry =
                            memoryCache.CreateEntry(
                                $"{graceIds.OrganizationId}deleted",
                                Value = MemoryCache.ExistsValue,
                                AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            )

                        return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the organization is not deleted.
        let organizationIsNotDeleted<'T> context correlationId (error: 'T) =
            task {
                match! organizationIsDeleted context correlationId error with
                | Ok _ -> return Error error
                | Error _ -> return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

    module Repository =

        /// Validates that the given RepositoryId exists in the database.
        let repositoryIdExists<'T> (repositoryId: string) correlationId (error: 'T) =
            task {
                let mutable repositoryGuid = Guid.Empty

                if (not <| String.IsNullOrEmpty(repositoryId))
                    && Guid.TryParse(repositoryId, &repositoryGuid)
                then
                    match memoryCache.GetRepositoryIdEntry repositoryGuid with
                    | Some value ->
                        match value with 
                        | MemoryCache.ExistsValue -> return Ok()
                        | MemoryCache.DoesNotExistValue -> return Error error
                        | _ -> return! repositoryExists repositoryId correlationId |> optionToResult error
                    | None -> return! repositoryExists repositoryId correlationId |> optionToResult error
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given repositoryId does not already exist in the database.
        let repositoryIdDoesNotExist<'T> (repositoryId: string) correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(repositoryId) then
                    match! repositoryIdExists repositoryId correlationId error with
                    | Ok _ -> return Error error
                    | Error _ -> return Ok()
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the repository exists.
        let repositoryExists<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId (error: 'T) =
            task {
                let mutable repositoryGuid = Guid.Empty

                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    if Guid.TryParse(repositoryId, &repositoryGuid) then
                        let exists = memoryCache.Get<string>(repositoryGuid)
                        match exists with
                        | MemoryCache.ExistsValue -> return Ok()
                        | MemoryCache.DoesNotExistValue -> return Error error
                        | _ ->
                            let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId(repositoryId), ActorName.Repository)

                            let! exists = repositoryActorProxy.Exists correlationId

                            if exists then
                                use newCacheEntry =
                                    memoryCache.CreateEntry(repositoryGuid, Value = MemoryCache.ExistsValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                                return Ok()
                            else
                                return Error error
                    else
                        return Ok()
                | None -> return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the repository is deleted.
        let repositoryIsDeleted<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId (error: 'T) =
            task {
                let mutable guid = Guid.Empty

                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    let repositoryGuid = Guid.Parse(repositoryId)
                    let isDeleted = memoryCache.Get<string>($"{repositoryGuid}deleted")
                    match isDeleted with
                    | MemoryCache.DoesNotExistValue -> return Ok()
                    | MemoryCache.ExistsValue -> return Error error
                    | _ ->
                        let actorId = Repository.GetActorId(repositoryGuid)

                        let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)

                        let! isDeleted = repositoryActorProxy.IsDeleted correlationId

                        if isDeleted then
                            use newCacheEntry =
                                memoryCache.CreateEntry($"{repositoryGuid}deleted", Value = MemoryCache.DoesNotExistValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                            return Ok()
                        else
                            use newCacheEntry =
                                memoryCache.CreateEntry($"{repositoryGuid}deleted", Value = MemoryCache.ExistsValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                            return Error error
                | None -> return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the repository is not deleted.
        let repositoryIsNotDeleted<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId (error: 'T) =
            task {
                match! repositoryIsDeleted ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId error with
                | Ok _ -> return Error error
                | Error _ -> return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        let repositoryNameIsUnique<'T> ownerId ownerName organizationId organizationName repositoryName correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(repositoryName) then
                    match! repositoryNameIsUnique ownerId ownerName organizationId organizationName repositoryName correlationId with
                    | Ok isUnique -> if isUnique then return Ok() else return Error error
                    | Error internalError ->
                        logToConsole internalError
                        return Error error
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

    module Branch =

        /// Validates that the given branchId exists in the database.
        let branchIdExists<'T> (branchId: string) correlationId (error: 'T) =
            task {
                let mutable branchGuid = Guid.Empty

                if (not <| String.IsNullOrEmpty(branchId)) && Guid.TryParse(branchId, &branchGuid) then
                    match memoryCache.GetBranchIdEntry branchGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.ExistsValue -> return Ok()
                        | MemoryCache.DoesNotExistValue -> return Error error
                        | _ -> return! branchExists branchId correlationId |> optionToResult error
                    | None -> return! branchExists branchId correlationId |> optionToResult error
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given branchId does not exist in the database.
        let branchIdDoesNotExist<'T> (branchId: string) correlationId (error: 'T) =
            task {
                let mutable guid = Guid.Empty

                if (not <| String.IsNullOrEmpty(branchId)) && Guid.TryParse(branchId, &guid) then
                    let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(ActorId(branchId), ActorName.Branch)

                    let! exists = branchActorProxy.Exists correlationId
                    if exists then return Error error else return Ok()
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the branch exists in the database.
        let branchExists<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName branchId branchName correlationId (error: 'T) =
            task {
                let mutable branchGuid = Guid.Empty

                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId branchId branchName correlationId with
                    | Some branchId ->
                        if Guid.TryParse(branchId, &branchGuid) then
                            let exists = memoryCache.Get<string>(branchGuid)

                            match exists with
                            | MemoryCache.ExistsValue -> return Ok()
                            | MemoryCache.DoesNotExistValue -> return Error error
                            | _ ->
                                let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(ActorId(branchId), ActorName.Branch)

                                let! exists = branchActorProxy.Exists correlationId

                                if exists then
                                    use newCacheEntry =
                                        memoryCache.CreateEntry(branchGuid, Value = MemoryCache.ExistsValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                                    return Ok()
                                else
                                    return Error error
                        else
                            return Error error
                    | None -> return Error error
                | None -> return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that a branch allows a specific reference type.
        let branchAllowsReferenceType<'T>
            ownerId
            ownerName
            organizationId
            organizationName
            repositoryId
            repositoryName
            branchId
            branchName
            (referenceType: ReferenceType)
            correlationId
            (error: 'T)
            =
            task {
                let mutable guid = Guid.Empty

                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId branchId branchName correlationId with
                    | Some branchId ->
                        let mutable allowed = new obj ()

                        if memoryCache.TryGetValue($"{branchId}{referenceType}Allowed", &allowed) then
                            let allowed = allowed :?> bool
                            if allowed then return Ok() else return Error error
                        else
                            let actorId = ActorId(branchId)

                            let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)

                            let! branchDto = branchActorProxy.Get correlationId

                            let allowed =
                                match referenceType with
                                | Promotion -> if branchDto.PromotionEnabled then true else false
                                | Commit -> if branchDto.CommitEnabled then true else false
                                | Checkpoint -> if branchDto.CheckpointEnabled then true else false
                                | Save -> if branchDto.SaveEnabled then true else false
                                | Tag -> if branchDto.TagEnabled then true else false
                                | External -> if branchDto.ExternalEnabled then true else false
                                | Rebase -> true // Rebase is always allowed.

                            use newCacheEntry =
                                memoryCache.CreateEntry(
                                    $"{branchId}{referenceType}Allowed",
                                    Value = allowed,
                                    AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                                )

                            if allowed then return Ok() else return Error error
                    | None -> return Error error
                | None -> return Error error
            }
            |> ValueTask<Result<unit, 'T>>


        /// Validates that a branch allows assign to create promotion references.
        let branchAllowsAssign<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName branchId branchName correlationId (error: 'T) =
            task {
                let mutable guid = Guid.Empty

                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId branchId branchName correlationId with
                    | Some branchId ->
                        let mutable allowed = new obj ()

                        if memoryCache.TryGetValue($"{branchId}AssignAllowed", &allowed) then
                            let allowed = allowed :?> bool
                            if allowed then return Ok() else return Error error
                        else
                            let actorId = ActorId(branchId)

                            let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)

                            let! branchDto = branchActorProxy.Get correlationId
                            let allowed = branchDto.AssignEnabled

                            use newCacheEntry =
                                memoryCache.CreateEntry($"{branchId}AssignAllowed", Value = allowed, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                            if allowed then return Ok() else return Error error
                    | None -> return Error error
                | None -> return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given branchName does not exist in the database.
        let branchNameDoesNotExist<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName branchName correlationId (error: 'T) =
            task {
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId String.Empty branchName correlationId with
                    | Some branchId -> return Error error
                    | None -> return Ok()
                | None -> return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that the given ReferenceId exists in the database.
        let referenceIdExists<'T> (referenceId: ReferenceId) correlationId (error: 'T) =
            task {
                if not <| (referenceId = Guid.Empty) then
                    let actorId = ActorId($"{referenceId}")

                    let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, ActorName.Reference)

                    let! exists = referenceActorProxy.Exists correlationId
                    if exists then return Ok() else return Error error
                else
                    return Ok()
            }
            |> ValueTask<Result<unit, 'T>>

    module DirectoryVersion =
        /// Validates that the given DirectoryId exists in the database.
        let directoryIdExists<'T> (directoryId: Guid) correlationId (error: 'T) =
            task {
                let exists = memoryCache.Get<string>(directoryId)

                match exists with
                | MemoryCache.ExistsValue -> return Ok()
                | MemoryCache.DoesNotExistValue -> return Error error
                | _ ->
                    let directoryVersionActorProxy =
                        ApplicationContext.actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(DirectoryVersion.GetActorId(directoryId), ActorName.DirectoryVersion)

                    let! exists = directoryVersionActorProxy.Exists correlationId

                    if exists then
                        use newCacheEntry = memoryCache.CreateEntry(directoryId, Value = MemoryCache.ExistsValue, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)

                        return Ok()
                    else
                        return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that all of the given DirectoryIds exist in the database.
        let directoryIdsExist<'T> (directoryIds: List<DirectoryVersionId>) correlationId (error: 'T) =
            task {
                let mutable allExist = true
                let directoryIdStack = Queue<DirectoryVersionId>(directoryIds)

                while directoryIdStack.Count > 0 && allExist do
                    let directoryId = directoryIdStack.Dequeue()
                    let directoryVersionActorProxy = actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(DirectoryVersion.GetActorId(directoryId), ActorName.DirectoryVersion)

                    let! exists = directoryVersionActorProxy.Exists correlationId
                    allExist <- exists

                if allExist then return Ok() else return Error error
            }
            |> ValueTask<Result<unit, 'T>>

        /// Validates that a directory version with the provided Sha256Hash exists in a repository.
        let sha256HashExists<'T> repositoryId sha256Hash correlationId (error: 'T) =
            task {
                let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(Repository.GetActorId(repositoryId), ActorName.Repository)

                match! getDirectoryBySha256Hash repositoryId sha256Hash correlationId with
                | Some directoryVersion -> return Ok()
                | None -> return Error error
            }
            |> ValueTask<Result<unit, 'T>>
