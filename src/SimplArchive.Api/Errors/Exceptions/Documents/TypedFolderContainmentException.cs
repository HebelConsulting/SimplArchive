using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A typed folder (Notes / Addressbook / Calendar) admits only its own item type, and such an item lives only
// there (#562/#564). Its own wire code because the caller's remedy is completely different from a name
// collision's: rename versus file it somewhere else. Previously these refusals came out as
// DOCUMENT_NAME_CONFLICT, which sent the caller looking for a clash that did not exist.
public sealed class TypedFolderContainmentException : DocumentException
{
    public TypedFolderContainmentException(string message)
        : base("TYPED_FOLDER_CONTAINMENT", StatusCodes.Status409Conflict, message)
    {
    }
}
