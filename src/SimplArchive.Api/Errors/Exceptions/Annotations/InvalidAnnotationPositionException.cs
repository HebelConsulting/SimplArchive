using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class InvalidAnnotationPositionException : AnnotationException
{
    public InvalidAnnotationPositionException()
        : base("INVALID_ANNOTATION_POSITION", StatusCodes.Status400BadRequest, "The note position must be within the page (0..1).")
    {
    }
}
