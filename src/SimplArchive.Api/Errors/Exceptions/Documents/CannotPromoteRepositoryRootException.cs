using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// A repository root (ParentId == null) has no primary home to demote to a reference, so it cannot be re-homed
// via the set-primary-location action (ADR 0506).
public sealed class CannotPromoteRepositoryRootException : DocumentException
{
    public CannotPromoteRepositoryRootException()
        : base("CANNOT_PROMOTE_REPOSITORY_ROOT", StatusCodes.Status409Conflict,
            "A repository root has no primary location to change.")
    {
    }
}
