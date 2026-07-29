using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Thrown when a folder contents-sort-order value isn't one of the defined options (ADR "Per-folder contents
// sort order").
public sealed class InvalidContentsSortOrderException : DocumentException
{
    public InvalidContentsSortOrderException()
        : base("INVALID_CONTENTS_SORT_ORDER", StatusCodes.Status400BadRequest, "The contents sort order is not a recognized option.")
    {
    }
}
