using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class EmptyAnnotationException : AnnotationException
{
    public EmptyAnnotationException()
        : base("EMPTY_ANNOTATION", StatusCodes.Status400BadRequest, "A note cannot be empty.")
    {
    }
}
