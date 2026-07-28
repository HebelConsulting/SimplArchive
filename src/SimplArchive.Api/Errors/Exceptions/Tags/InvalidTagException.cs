using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tags;

// A malformed catalog tag: a blank/over-long name, a bad colour, or an invalid merge target.
public sealed class InvalidTagException : TagException
{
    public InvalidTagException(string message)
        : base("INVALID_TAG", StatusCodes.Status400BadRequest, message)
    {
    }
}
