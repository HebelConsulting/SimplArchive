namespace SimplArchive.Api.Errors.Exceptions.Search;

// Base class for search-query validation errors (ADRs "Typed field filters in search" / "System-field search").
// Inherits from ApiException so the global handler translates it to an RFC 7807 response; concrete errors inherit
// from this so a caller can `catch (SearchException)` for the whole area. See the exception-type principle in
// CLAUDE.md.
public abstract class SearchException : ApiException
{
    protected SearchException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
