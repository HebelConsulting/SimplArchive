namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Base class for intray (staging-area) errors (ADR "S3-backed inbox"). Inherits from ApiException so the global
// handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (IntrayException)`. See the exception-type principle in CLAUDE.md.
public abstract class IntrayException : ApiException
{
    protected IntrayException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
