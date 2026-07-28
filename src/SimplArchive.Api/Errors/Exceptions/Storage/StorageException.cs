namespace SimplArchive.Api.Errors.Exceptions.Storage;

// Base class for object-storage / quota errors (ADR "Per-tenant storage quota"). Inherits from ApiException so
// the global handler translates it to an RFC 7807 response; concrete errors (e.g. StorageQuotaExceededException)
// inherit from this so a caller can `catch (StorageException)` for the whole area. See the exception-type
// principle in CLAUDE.md.
public abstract class StorageException : ApiException
{
    protected StorageException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
