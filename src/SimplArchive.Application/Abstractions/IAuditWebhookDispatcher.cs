namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Streams a tenant's newly-committed audit events to its configured SIEM webhook (ADR "Audit webhook
/// streaming"), off the request path. Delivers the contiguous run of events past the tenant's delivery
/// checkpoint, signing each, and advances the checkpoint after each success — durable + at-least-once, the same
/// per-tenant-Sequence-checkpoint pattern as the WORM archiver. A no-op for a tenant with no webhook configured.
/// Returns the number of events delivered.
/// </summary>
public interface IAuditWebhookDispatcher
{
    Task<int> DispatchAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
