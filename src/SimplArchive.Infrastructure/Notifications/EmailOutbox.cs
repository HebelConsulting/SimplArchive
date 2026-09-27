namespace SimplArchive.Infrastructure.Notifications;

/// <summary>
/// One notification owed an email (ADR 0840). The queue holds what is OWED; the notification keeps what
/// HAPPENED.
/// </summary>
/// <remarks>
/// <para>
/// Email used to run off the <c>Notification</c> row itself — <c>EmailedAt == null &amp;&amp; EmailFailedAt ==
/// null</c> was the pending set — which read as economical and cost three things. The pending predicate could
/// not be compare-and-swapped, so two sweeps in two instances both sent (ADR 0836); "suppressed by the
/// recipient's preference" had to be recorded as <c>EmailedAt</c>, so the row claimed an email that was never
/// sent; and <c>AddOrCoalesceAsync</c> became a second consumer of a column it does not care about, having to
/// leave it alone so a digest is emailed once with its final count.
/// </para>
/// <para>
/// A row here dissolves all three. The state to claim is the queue's, not the notification's. A muted
/// notification has its row DELETED and is stamped with nothing, so it says truthfully that it was never
/// emailed. And coalescing stops being an email concern entirely: the row sits un-drained, and whenever it
/// drains it reads whatever <c>EventCount</c> has become.
/// </para>
/// <para>
/// <b>The row is deleted in every terminal case</b> — sent, suppressed, abandoned — following
/// <c>SearchIndexOutbox</c> and <c>SearchablePdfOutbox</c>, which also drain by deleting. So an empty queue
/// means "nothing is owed", never "everything was sent", and the durable record of what happened stays on the
/// notification (<c>EmailedAt</c> / <c>EmailFailedAt</c>) where a human reads it.
/// </para>
/// <para>
/// <b>Deliberately NOT <c>ITenantScoped</c></b> — the sweep drains every tenant's rows and sets the tenant
/// context per row, the same reason <c>SearchIndexOutbox</c> is not scoped. And deliberately NOT
/// <c>IConcurrencyTracked</c>: this is the "append-only or machine-owned" exemption, since nobody edits a queue
/// row, and a token would actively hurt — the claim is an <c>ExecuteUpdate</c>, which ADR 0836 shows turns a
/// tracked row's contention into a lost batch rather than a lost duplicate.
/// </para>
/// </remarks>
public sealed class EmailOutbox
{
    public Guid Id { get; set; }

    /// <summary>The notification to email. Unique — one row per notification, which is what makes a duplicate
    /// ENQUEUE impossible (a duplicate SEND is prevented by the claim, not by this).</summary>
    public Guid NotificationId { get; set; }

    /// <summary>The notification's tenant, so the sweep can set the tenant context before tenant-filtered
    /// queries run — the same reason <c>SearchIndexOutbox</c> carries one.</summary>
    public Guid TenantId { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    /// <summary>
    /// Whether this row is waiting or being worked on. The THIRD state ADR 0836 says this sweep needs: a claim
    /// that used the success marker would record a failed send as sent, so the marker cannot be <c>EmailedAt</c>.
    /// </summary>
    public EmailOutboxState State { get; set; } = EmailOutboxState.Pending;

    /// <summary>When the row was claimed, so a claim abandoned by a dying process can be reclaimed. Null while
    /// <see cref="State"/> is <see cref="EmailOutboxState.Pending"/>.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// Failed (or abandoned) attempts so far — moved here from <c>Notification.EmailAttempts</c>, which was
    /// bookkeeping about a delivery living on the thing being delivered. Without a count there is nothing to
    /// distinguish "the server was down for a minute" from "this address cannot receive mail", and the second
    /// kind never leaves the queue.
    /// </summary>
    public int Attempts { get; set; }
}

/// <summary>
/// Starts at ONE, on purpose. A <c>Pending = 0</c> beside a store default is the shape that has bitten this
/// codebase before: EF cannot tell "the CLR default" from "not set", so a default-valued enum becomes
/// unstorable. Starting at 1 means the zero value is never legal and a row that somehow holds it is visibly
/// wrong rather than silently pending.
/// </summary>
public enum EmailOutboxState
{
    /// <summary>Waiting for a sweep. The only state a claim may compare-and-swap FROM.</summary>
    Pending = 1,

    /// <summary>Claimed by a sweep, which is sending it now. The exclusion itself: a second sweep sees this and
    /// leaves the row alone, so no row lock has to be held across an SMTP call.</summary>
    Sending = 2,
}
