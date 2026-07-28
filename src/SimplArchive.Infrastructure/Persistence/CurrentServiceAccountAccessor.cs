using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Scoped, settable ICurrentServiceAccountAccessor implementation — mirrors CurrentTenantAccessor
/// exactly. Set by Api middleware from the JWT's Subject claim per request (see ADR: ServiceAccount
/// request authentication foundation).
/// </summary>
public class CurrentServiceAccountAccessor : ICurrentServiceAccountAccessor
{
    public Guid? ServiceAccountId { get; set; }
}
