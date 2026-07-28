using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class CannotDeleteAnnotationException : AnnotationException
{
    public CannotDeleteAnnotationException()
        : base("CANNOT_DELETE_ANNOTATION", StatusCodes.Status403Forbidden, "Only the author or an editor can delete a note.")
    {
    }
}
