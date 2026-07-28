using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Acl;

// Thrown when a caller tries to break/restore ACL inheritance on a repository root (ADR "Manage-access UI ..."
// inheritance follow-up). A root has no parent to inherit from — its own grants are always the fallback — so
// toggling inheritance there is meaningless and, for restore, would dangerously wipe the root's own grants.
public sealed class CannotChangeRootInheritanceException : AclException
{
    public CannotChangeRootInheritanceException()
        : base("CANNOT_CHANGE_ROOT_INHERITANCE", StatusCodes.Status400BadRequest, "Inheritance can't be changed on a repository root — it has no parent to inherit from.")
    {
    }
}
