using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class InvalidAnnotationKindException : AnnotationException
{
    public InvalidAnnotationKindException()
        : base("INVALID_ANNOTATION_KIND", StatusCodes.Status400BadRequest, "The annotation kind is not recognized.")
    {
    }
}
