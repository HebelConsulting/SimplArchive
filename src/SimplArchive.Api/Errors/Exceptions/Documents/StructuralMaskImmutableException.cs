using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A Mailbox, IMAP Special folder, Notebook or Notebook Section keeps its type once it has one. Its own wire
// code because the caller's remedy is nothing like a missing field's or a name clash's: there is no value to
// supply and no name to change — the folder simply is what it is, and the way out is to move its contents.
public sealed class StructuralMaskImmutableException : DocumentException
{
    public StructuralMaskImmutableException(string message)
        : base("STRUCTURAL_MASK_IMMUTABLE", StatusCodes.Status409Conflict, message)
    {
    }
}
