using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.References;

// Thrown when a reference would point at itself or one of its own descendants (ADR "Desktop drag-and-drop move
// and reference").
public sealed class InvalidReferenceTargetException : ReferenceException
{
    public InvalidReferenceTargetException()
        : base("INVALID_REFERENCE_TARGET", StatusCodes.Status400BadRequest, "Cannot reference an item into itself or one of its own descendants.")
    {
    }
}
