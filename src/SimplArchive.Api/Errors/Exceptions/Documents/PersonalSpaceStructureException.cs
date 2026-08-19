using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The personal space's first level holds only the folders it was provisioned with, and those cannot be deleted
// or moved out (#596/#634). Its own wire code because the caller's remedy is nothing like a name collision's:
// file it inside My Documents, rather than pick another name. These refusals came out as
// DOCUMENT_NAME_CONFLICT until this existed — "a document with this name already exists" about a name that was
// a fresh GUID, which is the same false cause TYPED_FOLDER_CONTAINMENT was created to stop.
public sealed class PersonalSpaceStructureException : DocumentException
{
    public PersonalSpaceStructureException(string message)
        : base("PERSONAL_SPACE_STRUCTURE", StatusCodes.Status409Conflict, message)
    {
    }
}
