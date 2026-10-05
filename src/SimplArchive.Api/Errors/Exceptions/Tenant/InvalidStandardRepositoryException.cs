using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

// The standard repository must be a live, shared repository of this tenant (ADR 0892): a top-level document that is
// not a personal space and not in the recycle bin. Anything else is refused rather than stored, because the core
// files into it unattended and would otherwise file into a place nobody can see.
public sealed class InvalidStandardRepositoryException : TenantException
{
    public InvalidStandardRepositoryException()
        : base("STANDARD_REPOSITORY_INVALID", StatusCodes.Status400BadRequest,
            "The standard repository must be one of this tenant's shared repositories — a top-level folder that is "
            + "not a personal space and not in the recycle bin.")
    {
    }
}
