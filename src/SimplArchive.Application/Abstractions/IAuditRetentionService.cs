namespace SimplArchive.Application.Abstractions;

// Purges a tenant's audit events older than its retention window (ADR "Audit trail retention and purge"),
// keeping the tamper-evidence chain consistent: it deletes only the oldest contiguous Sequence prefix (never
// the chain tip, so the recorder's Sequence high-water is preserved) and advances the tenant's retained-window
// checkpoint in the same transaction. Takes an explicit tenant so the background worker can span all tenants;
// the manual-purge endpoint passes the caller's tenant. Returns the number of events purged (0 when retention
// is disabled, the tenant is unknown, or nothing is old enough).
public interface IAuditRetentionService
{
    Task<int> PurgeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
