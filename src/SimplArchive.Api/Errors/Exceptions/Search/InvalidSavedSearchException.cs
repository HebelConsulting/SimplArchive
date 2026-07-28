using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Search;

// A saved-search create was missing a name or the query to save (ADR "Saved searches").
public sealed class InvalidSavedSearchException : SearchException
{
    public InvalidSavedSearchException()
        : base("INVALID_SAVED_SEARCH", StatusCodes.Status400BadRequest, "A saved search needs a name and a query.")
    {
    }
}
