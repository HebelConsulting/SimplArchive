using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Search;

// The caller already has a saved search with this name (ADR "Saved searches") — names are unique per user.
public sealed class SavedSearchNameConflictException : SearchException
{
    public SavedSearchNameConflictException()
        : base("SAVED_SEARCH_NAME_CONFLICT", StatusCodes.Status409Conflict, "You already have a saved search with that name.")
    {
    }
}
