using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Import;

// Thrown when an import runs with no tenant in context (a defensive guard — an authenticated import always has
// one). ADR "Repository / folder import".
public sealed class NoTenantException : ImportException
{
    public NoTenantException()
        : base("NO_TENANT", StatusCodes.Status400BadRequest, "No tenant in context.")
    {
    }
}
