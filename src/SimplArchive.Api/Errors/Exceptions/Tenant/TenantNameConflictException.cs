using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

// A tenant name collides with another active tenant. Thrown from two contexts sharing the TENANT_NAME_CONFLICT
// wire code; the static factories preserve each site's message.
public sealed class TenantNameConflictException : TenantException
{
    private TenantNameConflictException(string message)
        : base("TENANT_NAME_CONFLICT", StatusCodes.Status409Conflict, message)
    {
    }

    public static TenantNameConflictException OnRename() =>
        new("Another active tenant already uses this name.");

    public static TenantNameConflictException OnCreate() =>
        new("A tenant with this name already exists.");
}
