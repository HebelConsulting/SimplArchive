using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.References;

// Thrown when the item a reference would point at doesn't exist / isn't visible (ADR "Desktop drag-and-drop move
// and reference").
public sealed class ReferenceTargetNotFoundException : ReferenceException
{
    public ReferenceTargetNotFoundException()
        : base("REFERENCE_TARGET_NOT_FOUND", StatusCodes.Status404NotFound, "The referenced item was not found.")
    {
    }
}
