namespace SimplArchive.Infrastructure.Notifications;

/// <summary>
/// A notice mailed to an address that is NOT a user of the tenant (ABI 1.10, ADR 0902): a module's customer contact.
/// Written in the transaction of the act that queued it, so a rolled-back act sends nothing, and drained by
/// <see cref="ExternalEmailDispatcher"/> in the shape of <see cref="EmailOutbox"/> (ADR 0840): claimed by
/// compare-and-swap, a stale claim reclaimed, a transient failure retried, the row deleted when done.
/// </summary>
/// <remarks>
/// Its own table rather than <see cref="EmailOutbox"/>, which is a pointer to a user's <c>Notification</c> and is drained
/// by joining that user. Not tenant-scoped and not concurrency-tracked: a machine-owned queue row whose only writers are
/// the enqueue and the claim, the owner's exemption (2026-10-07), as for <see cref="EmailOutbox"/>.
/// </remarks>
public sealed class ExternalEmailOutbox
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The module that queued it, for the log and the audit trail.</summary>
    public required string ModuleId { get; set; }

    public required string RecipientAddress { get; set; }

    /// <summary>Already carries the tenant's name as a prefix.</summary>
    public required string Subject { get; set; }

    public required string Body { get; set; }

    public string? ReplyTo { get; set; }

    /// <summary>The document VERSION whose content is attached: frozen when queued, read through the storage seam when
    /// sent, so at-rest encryption applies.</summary>
    public Guid? AttachmentVersionId { get; set; }

    public string? AttachmentFileName { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    public EmailOutboxState State { get; set; } = EmailOutboxState.Pending;

    public DateTimeOffset? ClaimedAt { get; set; }

    public int Attempts { get; set; }
}
