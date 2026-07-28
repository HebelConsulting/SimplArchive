using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class CannotPurgeActiveException : DocumentException
{
    public CannotPurgeActiveException()
        : base("CANNOT_PURGE_ACTIVE", StatusCodes.Status400BadRequest, "Only a recycle-bin item can be purged. Delete it first.")
    {
    }
}
