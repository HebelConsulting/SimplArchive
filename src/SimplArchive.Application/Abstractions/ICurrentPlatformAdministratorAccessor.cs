namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Resolves the current PlatformAdministrator for the in-flight request, read from the JWT's Subject
/// claim when the token carries the platform-admin marker claim (see ADR "Tenant onboarding and
/// platform-admin mechanism"). Mirrors ICurrentServiceAccountAccessor exactly — mutually exclusive with
/// it per request, since a token is either a ServiceAccount's or a PlatformAdministrator's, never both.
/// </summary>
public interface ICurrentPlatformAdministratorAccessor
{
    Guid? PlatformAdministratorId { get; }
}
