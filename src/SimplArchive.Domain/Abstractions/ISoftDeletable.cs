namespace SimplArchive.Domain.Abstractions;

/// <summary>
/// Marks an entity as soft-deletable (recycle-bin model, see ADR: Retention / data lifecycle policy).
/// Every entity implementing this interface automatically gets a global EF Core query filter excluding
/// DeletedAt != null rows (see SimplArchiveDbContext) — the sole soft-delete enforcement point, mirroring
/// ITenantScoped's exact reflection-based shape. Callers that need to see deleted rows (e.g. a recycle-bin
/// listing endpoint) opt out explicitly via IgnoreQueryFilters(), same pattern already used for
/// cross-tenant lookups.
/// </summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}
