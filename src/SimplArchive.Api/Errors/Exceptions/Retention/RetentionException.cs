namespace SimplArchive.Api.Errors.Exceptions.Retention;

// Base class for retention disposition-review errors (ADR "Retention review-before-disposition"). Inherits from
// ApiException so the global handler translates it to an RFC 7807 response; concrete errors inherit from this so
// a caller can `catch (RetentionException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class RetentionException : ApiException
{
    protected RetentionException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
