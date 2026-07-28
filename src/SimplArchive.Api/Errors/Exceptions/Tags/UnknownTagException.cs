using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tags;

public sealed class UnknownTagException : TagException
{
    public UnknownTagException(string tag)
        : base("UNKNOWN_TAG", StatusCodes.Status400BadRequest, $"'{tag}' is not in the tag catalog (this tenant restricts tags to the catalog).")
    {
    }
}
