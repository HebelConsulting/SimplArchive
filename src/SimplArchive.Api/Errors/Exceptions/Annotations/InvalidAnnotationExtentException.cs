using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class InvalidAnnotationExtentException : AnnotationException
{
    public InvalidAnnotationExtentException()
        : base("INVALID_ANNOTATION_EXTENT", StatusCodes.Status400BadRequest, "A shape annotation needs a width and height in [-1, 1] with a non-degenerate size.")
    {
    }
}
