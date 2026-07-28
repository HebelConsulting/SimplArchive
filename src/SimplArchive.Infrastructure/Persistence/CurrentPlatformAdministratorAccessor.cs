using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Scoped, settable ICurrentPlatformAdministratorAccessor implementation — mirrors
/// CurrentServiceAccountAccessor exactly. Set by Api middleware from the JWT's Subject claim per request
/// when the token carries the platform-admin marker claim (see ADR "Tenant onboarding and platform-admin
/// mechanism").
/// </summary>
public class CurrentPlatformAdministratorAccessor : ICurrentPlatformAdministratorAccessor
{
    public Guid? PlatformAdministratorId { get; set; }
}
