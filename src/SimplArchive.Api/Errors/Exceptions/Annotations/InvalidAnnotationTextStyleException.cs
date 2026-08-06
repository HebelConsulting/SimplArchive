using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Annotations;

// Thrown when a supplied text style can't be stored as given (ADR 0542): a style on a kind that draws no text,
// a non-positive or implausible font size, an undefined size basis, or an over-long font family.
public sealed class InvalidAnnotationTextStyleException : AnnotationException
{
    public InvalidAnnotationTextStyleException(string reason)
        : base("INVALID_ANNOTATION_TEXT_STYLE", StatusCodes.Status400BadRequest, $"The annotation text style is invalid: {reason}")
    {
    }
}
