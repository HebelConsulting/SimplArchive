using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tags;

public sealed class TagNotFoundException : TagException
{
    public TagNotFoundException()
        : base("TAG_NOT_FOUND", StatusCodes.Status404NotFound, "No catalog tag with that id exists.")
    {
    }
}
