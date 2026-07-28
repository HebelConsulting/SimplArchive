using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.References;

// Thrown when the item is already referenced in the target folder (the UNIQUE (folder, target) constraint, ADR
// "Desktop drag-and-drop move and reference").
public sealed class ReferenceAlreadyExistsException : ReferenceException
{
    public ReferenceAlreadyExistsException()
        : base("REFERENCE_ALREADY_EXISTS", StatusCodes.Status409Conflict, "This item is already referenced in this folder.")
    {
    }
}
