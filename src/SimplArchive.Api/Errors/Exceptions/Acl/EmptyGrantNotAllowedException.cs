using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Acl;

// Thrown when an ACL grant would set every right to false (ADR "Reject empty AclEntry grants") — a client-side
// backstop for the CK_AclEntries_AtLeastOneRight DB constraint.
public sealed class EmptyGrantNotAllowedException : AclException
{
    public EmptyGrantNotAllowedException()
        : base("EMPTY_GRANT_NOT_ALLOWED", StatusCodes.Status400BadRequest, "At least one right must be granted.")
    {
    }
}
