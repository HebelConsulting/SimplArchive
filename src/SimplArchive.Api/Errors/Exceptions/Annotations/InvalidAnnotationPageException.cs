using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class InvalidAnnotationPageException : AnnotationException
{
    public InvalidAnnotationPageException()
        : base("INVALID_ANNOTATION_PAGE", StatusCodes.Status400BadRequest, "The page index cannot be negative.")
    {
    }
}
