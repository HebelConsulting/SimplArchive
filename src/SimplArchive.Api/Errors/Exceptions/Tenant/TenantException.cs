namespace SimplArchive.Api.Errors.Exceptions.Tenant;

// Base class for tenant settings + provisioning errors (ADRs "Tenant-admin settings tab" / "Tenant onboarding
// and platform admin"). Inherits from ApiException so the global handler translates it to an RFC 7807 response;
// concrete errors inherit from this so a caller can `catch (TenantException)` for the whole area. See the
// exception-type principle in CLAUDE.md.
public abstract class TenantException : ApiException
{
    protected TenantException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
