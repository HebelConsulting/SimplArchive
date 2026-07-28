namespace SimplArchive.Domain.Abstractions;

/// <summary>
/// Marks an entity as backing HTTP ETag/If-Match optimistic concurrency (see ADR: ETag / If-Match
/// optimistic concurrency). Every entity implementing this interface automatically gets ConcurrencyToken
/// configured as an EF Core concurrency token (see SimplArchiveDbContext) and regenerated to a fresh
/// value on every Added/Modified SaveChanges — never set it manually, same precedent as
/// MaskVersion.VersionNumber/IsCurrent.
/// </summary>
public interface IConcurrencyTracked
{
    Guid ConcurrencyToken { get; set; }
}
