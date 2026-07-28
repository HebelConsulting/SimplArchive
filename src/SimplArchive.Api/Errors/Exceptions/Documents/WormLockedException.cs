using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

public sealed class WormLockedException : DocumentException
{
    public WormLockedException()
        : base("WORM_LOCKED", StatusCodes.Status409Conflict, "This item has a version under a WORM retention or legal hold and cannot be purged until it is released or expires.")
    {
    }
}
