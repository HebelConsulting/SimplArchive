namespace SimplArchive.Api.Errors.Exceptions.Concurrency;

// Base class for optimistic-concurrency (ETag / If-Match) errors, shared by every IConcurrencyTracked resource
// (ADR "ETag / If-Match concurrency"). Inherits from ApiException so the global handler translates it to an RFC
// 7807 response; concrete errors inherit from this so a caller can `catch (ConcurrencyException)`. See the
// exception-type principle in CLAUDE.md.
public abstract class ConcurrencyException : ApiException
{
    protected ConcurrencyException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
