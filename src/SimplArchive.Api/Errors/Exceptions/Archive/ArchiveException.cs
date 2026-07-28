namespace SimplArchive.Api.Errors.Exceptions.Archive;

// Base class for zip-archive browsing errors (ADR "Zip file browsing"). Inherits from ApiException so the global
// handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (ArchiveException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class ArchiveException : ApiException
{
    protected ArchiveException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
