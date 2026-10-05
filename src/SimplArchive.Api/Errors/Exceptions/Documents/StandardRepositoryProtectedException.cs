using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

// The tenant's standard repository cannot be deleted (ADR 0892). Its own wire code because the remedy is nothing
// like any other refusal's: no right is missing and no name clashes — another repository has to become the
// standard one first, which is a tenant setting.
public sealed class StandardRepositoryProtectedException : DocumentException
{
    public StandardRepositoryProtectedException(string message)
        : base("STANDARD_REPOSITORY_CANNOT_BE_DELETED", StatusCodes.Status409Conflict, message)
    {
    }
}
