using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class NotAnnotationAuthorException : AnnotationException
{
    public NotAnnotationAuthorException()
        : base("NOT_ANNOTATION_AUTHOR", StatusCodes.Status403Forbidden, "Only the author can edit a note.")
    {
    }
}
