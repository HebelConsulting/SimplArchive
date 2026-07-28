namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Resolves the current ServiceAccount for the in-flight request, read from the JWT's Subject claim
/// (see ADR: ServiceAccount request authentication foundation). Implemented in the hosting layer;
/// mirrors ICurrentTenantAccessor exactly (interface exposes only a getter; the concrete settable class
/// is what middleware writes to).
/// </summary>
public interface ICurrentServiceAccountAccessor
{
    Guid? ServiceAccountId { get; }
}
