using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

public sealed class InvalidAnnotationPointsException : AnnotationException
{
    public InvalidAnnotationPointsException()
        : base("INVALID_ANNOTATION_POINTS", StatusCodes.Status400BadRequest, "A freehand annotation needs a path of at least two \"x,y\" points, each normalized to [0, 1].")
    {
    }
}
