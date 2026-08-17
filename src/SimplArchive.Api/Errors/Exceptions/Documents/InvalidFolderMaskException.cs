using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Thrown when a create-child request names a folder mask that is not one (#564 slice 2, ADR 0620). Only the
// FOLDER masks may be asked for by name — a caller cannot create a child already wearing an item mask, since
// what an item is gets decided by classifying its content, not by the caller asserting it.
public sealed class InvalidFolderMaskException : DocumentException
{
    public InvalidFolderMaskException(string requested)
        : base("INVALID_FOLDER_MASK", StatusCodes.Status400BadRequest,
            $"'{requested}' is not a folder type. Use one of: folder, calendar, addressbook, notes.")
    {
    }
}
