namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Resolves the current tenant for the in-flight request/operation, read from the JWT tenant claim
/// (see ADR: Multi-tenancy resolution strategy). Implemented in the hosting layer once auth exists;
/// consumed by SimplArchiveDbContext to drive the global tenant query filter.
/// </summary>
public interface ICurrentTenantAccessor
{
    Guid? TenantId { get; }
}
