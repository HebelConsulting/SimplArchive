namespace SimplArchive.Api.Errors.Exceptions.Import;

// Base class for repository/folder import errors (ADR "Repository / folder import"). Inherits from ApiException
// so the global handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (ImportException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class ImportException : ApiException
{
    protected ImportException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
