﻿namespace Grace.Shared.Resources

open Grace.Shared
open Grace.Shared.Resources.Text
open System
open System.Collections.Generic

module en_US =

    /// <summary>
    /// Returns the en_US localized string for each value of StringResourceName.
    /// </summary>
    /// <param name="stringName">The resource name of the string to return.</param>
    let getString stringResourceName =
        match stringResourceName with
        | AssignIsDisabled -> "This branch has disabled assign."
        | BranchAlreadyExists -> "The branch already exists."
        | BranchDoesNotExist -> "The branch was not found."
        | BranchIdIsRequired -> "The BranchId must be provided."
        | BranchIdsAreRequired -> "The list of BranchIds must not be empty."
        | BranchIsNotBasedOnLatestPromotion ->
            "The promotion failed because the current branch is not based on the latest promotion from the parent branch."
        | BranchNameAlreadyExists -> "A branch with the provided BranchName already exists."
        | BranchNameIsRequired -> "The BranchName must be provided."
        | CheckpointIsDisabled -> "This branch has disabled checkpoints."
        | CommitIsDisabled -> "This branch has disabled commits."
        | CreatingNewDirectoryVersions -> "Creating new directory versions, with updated SHA-256 hashes."
        | CreatingSaveReference -> "Creating a save reference to mark your current state before switching."
        | DeleteReasonIsRequired -> "The DeleteReason must be provided."
        | DescriptionIsRequired -> "The description must be provided."
        | DirectoryAlreadyExists -> "A directory with the provided DirectoryId already exists."
        | DirectoryDoesNotExist -> "The directory was not found."
        | DirectorySha256HashAlreadyExists -> "A directory with the provided SHA-256 hash already exists."
        | DuplicateCorrelationId ->
            "The CorrelationId sent was a duplicate of one already used to modify this object. Because this likely indicates a retry of an operation that has already succeeded, this is not allowed."
        | EitherBranchIdOrBranchNameIsRequired ->
            "Either a BranchId or a BranchName must be provided. If both are provided, BranchId will be used."
        | EitherDirectoryVersionIdOrSha256HashRequired ->
            "Either a DirectoryVersionId or a SHA-256 hash must be provided. If both are provided, DirectoryVersionId will be used."
        | EitherOrganizationIdOrOrganizationNameIsRequired ->
            "Either a OrganizationId or a OrganizationName must be provided. If both are provided, OrganizationId will be used."
        | EitherOwnerIdOrOwnerNameIsRequired ->
            "Either an OwnerId or an OwnerName must be provided. If both are provided, OwnerId will be used."
        | EitherRepositoryIdOrRepositoryNameIsRequired ->
            "Either a RepositoryId, or a RepositoryName, must be provided. If both are provided, RepositoryId will be used."
        | EitherToBranchIdOrToBranchNameIsRequired ->
            "Either a ToBranchId or a ToBranchName must be provided. If both are provided, ToBranchId will be used."
        | ExceptionCaught -> "An exception was caught while processing the request."
        | FailedCommunicatingWithObjectStorage -> "A failure occurred when communicating with object storage."
        | FailedCreatingEmptyDirectoryVersion ->
            "A server error occurred while attempting to create an empty initial directory version."
        | FailedCreatingInitialBranch -> "A server error occurred while attempting to create the initial branch."
        | FailedCreatingInitialPromotion -> "A server error occurred while attempting to create the initial promotion."
        | FailedRebasingInitialBranch -> "A server error occurred while attempting to rebase the initial branch."
        | FailedToGetUploadUrls ->
            "A server error occurred while retrieving the URLs to upload new files to object storage."
        | FailedToRetrieveBranch -> "A server error occurred while retrieving the branch information."
        | FailedUploadingFilesToObjectStorage -> "One or more files could not be uploaded to object storage."
        | FailedWhileApplyingEvent -> "A server error occurred while attempting to update the data transfer object."
        | FailedWhileSavingEvent -> "A server error occurred while attempting to save the event."
        | FilesMustNotBeEmpty -> "A non-empty list of files must be provided."
        | FileNotFoundInObjectStorage -> "The file was not found in object storage."
        | FileSha256HashDoesNotMatch ->
            "The provided SHA-256 hash of the file does not match the SHA-256 hash calculated by Grace Server."
        | GettingCurrentBranch -> "Getting the current branch from the server."
        | GettingLatestVersion -> "Getting the latest version of the branch you're switching to."
        | GraceConfigFileNotFound ->
            $"No {Constants.GraceConfigFileName} file found along current path. Please run `grace config write` if you would like to create one."
        | IndexFileNotFound ->
            "The Grace index file was not found. Please run `grace maintenance update-index` to re-create it."
        | InitialPromotionMessage -> "Initial, empty promotion."
        | InterprocessFileDeleted -> "Inter-process communication file deleted."
        | InvalidBranchId -> "The provided BranchId is not a valid Guid."
        | InvalidBranchName ->
            "The BranchName is not a valid Grace name. A valid object name in Grace has between 2 and 64 characters, has a letter for the first character ([A-Za-z]), and letters, numbers, or - for the rest ([A-Za-z0-9\-]{1,63})."
        | InvalidCheckpointDaysValue -> "The provided value for CheckpointDays is invalid."
        | InvalidDiffCacheDaysValue -> "The provided value for DiffCacheDays is invalid."
        | InvalidDirectoryVersionCacheDaysValue -> "The provided value for DirectoryVersionCacheDays is invalid."
        | InvalidDirectoryPath -> "The provided directory is not a valid directory path."
        | InvalidDirectoryId -> "The provided DirectoryId is not a valid Guid."
        | InvalidMaxCountValue -> "The provided value for MaxCount is invalid."
        | InvalidObjectStorageProvider -> "The provided object storage provider is not valid."
        | InvalidOrganizationId -> "The provided OrganizationId is not a valid Guid."
        | InvalidOrganizationName ->
            "The OrganizationName is not a valid Grace name. A valid object name in Grace has between 2 and 64 characters, has a letter for the first character [A-Za-z], and letters, numbers, or - for the rest [A-Za-z0-9\-]{1,63}."
        | InvalidOrganizationType -> "The OrganizationType provided is not a valid OrganizationType value."
        | InvalidOwnerId -> "The provided OwnerId is not a valid Guid."
        | InvalidOwnerName ->
            "The OwnerName is not a valid Grace name. A valid object name in Grace has between 2 and 64 characters, has a letter for the first character ([A-Za-z]), and letters, numbers, or - for the rest ([A-Za-z0-9\-]{1,63})."
        | InvalidOwnerType -> "The OwnerType provided is not a valid OwnerType value."
        | InvalidReferenceId -> "The provided ReferenceId is not a valid Guid."
        | InvalidReferenceType -> "The provided ReferenceType is not valid."
        | InvalidRepositoryId -> "The provided RepositoryId is not a valid Guid."
        | InvalidRepositoryName ->
            "The RepositoryName is not a valid Grace name. A valid object name in Grace has between 2 and 64 characters, has a letter for the first character ([A-Za-z]), and letters, numbers, or - for the rest ([A-Za-z0-9\-]{1,63})."
        | InvalidRepositoryStatus -> "The repository status provided is not valid."
        | InvalidSaveDaysValue -> "The provided value for SaveDays is invalid."
        | InvalidSearchVisibility -> "The SearchVisibility provided is not a valid SearchVisibility value."
        | InvalidServerApiVersion ->
            "The provided ServerApiVersion is not recognized. Please use a published Grace API version identifier."
        | InvalidSha256Hash -> "The provided SHA-256 hash is not a valid SHA-256 hash value."
        | InvalidSize ->
            "The provided size does not match the size calculated by adding the sizes of all files in the directory."
        | InvalidVisibilityValue -> "The provided visibility value is not valid."
        | PromotionIsDisabled -> "This branch has disabled promotions."
        | PromotionNotAvailableBecauseThereAreNoPromotableReferences ->
            "Promotion is not available because there are no commits or promotions in the current branch to promote to the parent branch."
        | MessageIsRequired -> "A message is required for this reference."
        | NotImplemented -> "This feature is not yet implemented."
        | ObjectCacheFileNotFound ->
            "The Grace object cache file was not found. Please run `grace maintenance scan` to recreate it."
        | ObjectStorageException -> "An exception occurred when communicating with the object storage provider."
        | OrganizationIdAlreadyExists -> "An Organization with the provided OrganizationId already exists."
        | OrganizationNameAlreadyExists -> "An organization with the same name and owner already exists."
        | OrganizationContainsRepositories ->
            "The organization cannot be deleted because it contains active repositories. Specify --force if you would like to recursively delete all repositories."
        | OrganizationDoesNotExist -> "The organization was not found."
        | OrganizationIdDoesNotExist -> "An Organization with the provided OrganizationId does not exist."
        | OrganizationIdIsRequired -> "The OrganizationId must be provided."
        | OrganizationIsDeleted -> "The organization is deleted."
        | OrganizationIsNotDeleted -> "The organization is not deleted."
        | OrganizationNameIsRequired -> "The OrganizationName must be provided."
        | OrganizationTypeIsRequired -> "The OrganizationType must be provided."
        | OwnerContainsOrganizations ->
            "The owner cannot be deleted because it contains active organizations. Specify --force if you would like to recursively delete all organizations, and their repositories."
        | OwnerDoesNotExist -> "The owner was not found."
        | OwnerIdAlreadyExists -> "An Owner with the provided OwnerId already exists."
        | OwnerIdDoesNotExist -> "An Owner with the provided OwnerId does not exist."
        | OwnerIdIsRequired -> "The OwnerId must be provided."
        | OwnerIsDeleted -> "The owner is deleted."
        | OwnerIsNotDeleted -> "The owner is not deleted."
        | OwnerNameAlreadyExists -> "An Owner with the provided OwnerName already exists."
        | OwnerNameIsRequired -> "The OwnerName must be provided."
        | OwnerTypeIsRequired -> "The OwnerType must be provided."
        | ParentBranchDoesNotExist -> "The parent branch provided does not exist."
        | ReadingGraceStatus -> "Reading the Grace status file."
        | ReferenceIdDoesNotExist -> "The given ReferenceId does not exist."
        | ReferenceIdsAreRequired -> "The list of ReferenceIds must not be empty."
        | ReferenceTypeMustBeProvided -> "The reference type cannot be an empty string."
        | RelativePathMustNotBeEmpty -> "The relative path of the directory cannot be an empty string."
        | RepositoryContainsBranches ->
            "The repository cannot be deleted because it contains active branches. Specify --force if you would like to recursively delete all branches."
        | RepositoryDoesNotExist -> "The repository was not found."
        | RepositoryIdAlreadyExists -> "A repository with the provided RepositoryId already exists."
        | RepositoryIdDoesNotExist -> "A repository with the provided RepositoryId does not exist."
        | RepositoryIdIsRequired -> "The RepositoryId must be provided."
        | RepositoryIsAlreadyInitialized -> "The repository is already initialized."
        | RepositoryIsDeleted -> "The repository is deleted."
        | RepositoryIsNotDeleted -> "The repository is not deleted."
        | RepositoryIsNotEmpty -> "The repository is not empty. Only empty repositories can be initialized."
        | RepositoryNameIsRequired -> "The RepositoryName must be provided."
        | RepositoryNameAlreadyExists -> "A repository with the provided name already exists."
        | SaveIsDisabled -> "This branch has disabled saves."
        | SavingDirectoryVersions -> "Saving the new directory versions on the server."
        | ScanningWorkingDirectory -> "Scanning your working directory for changes."
        | SearchVisibilityIsRequired -> "The SearchVisibility must be provided."
        | ServerRequestsMustIncludeXCorrelationIdHeader ->
            "Grace requires every server request to include an X-Correlation-Id header. This header should contain a unique string for each call."
        | Sha256HashDoesNotExist -> "The Sha256Hash value was not found."
        | Sha256HashDoesNotMatch ->
            "The provided SHA-256 hash for this directory version does not match the SHA-256 hash calculated by Grace Server."
        | Sha256HashIsRequired -> "The Sha256Hash value is required."
        | StringIsTooLong -> "The provided string is longer than allowed."
        | TagIsDisabled -> "This branch has disabled tags."
        | UnknownObjectStorageProvider -> "The object storage provider for this repository is unknown."
        | UpdatingWorkingDirectory -> "Updating your working directory to match the new branch."
        | UploadingFiles -> "Uploading new and updated files to object storage."
        | ValueMustBePositive -> "The value must be positive."
