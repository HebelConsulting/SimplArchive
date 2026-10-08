namespace SimplArchive.ModuleAbi;

/// <summary>
/// A presigned upload a module handed out (ABI 1.12, core ADR 0910): the client PUTs its bytes to <see cref="Url"/>
/// before <see cref="ExpiresAt"/>, straight to object storage, and the module then reads them with
/// <see cref="IModuleArchiveFacade.OpenUploadAsync"/> and files them with <see cref="IModuleArchiveFacade.FileUploadAsync"/>.
/// </summary>
/// <param name="UploadId">Opaque. Names the upload to the facade, and only for the module that began it.</param>
/// <param name="Url">The presigned PUT address the client uploads to.</param>
/// <param name="ExpiresAt">When the address stops accepting the PUT.</param>
/// <param name="MaxBytes">The largest upload the facade will open or file; a larger one is refused, not truncated.</param>
public sealed record ModuleUpload(string UploadId, Uri Url, DateTimeOffset ExpiresAt, long MaxBytes);

/// <summary>Nothing was uploaded under that id, or it expired and was swept. A module's client answer is a 409.</summary>
public sealed class ModuleUploadMissingException(string uploadId)
    : ModuleApiException("MODULE_UPLOAD_MISSING", 409, $"Nothing has been uploaded for {uploadId}, or the upload expired.");

/// <summary>The upload is larger than the module allowed when it began it. It is discarded.</summary>
public sealed class ModuleUploadTooLargeException(long size, long maxBytes)
    : ModuleApiException("MODULE_UPLOAD_TOO_LARGE", 413, $"The upload is {size} bytes; at most {maxBytes} are accepted.");
