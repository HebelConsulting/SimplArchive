using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class InvalidAnnotationColorException : AnnotationException
{
    public InvalidAnnotationColorException()
        : base("INVALID_ANNOTATION_COLOR", StatusCodes.Status400BadRequest, "The note colour must be a #RRGGBB hex value.")
    {
    }
}
