using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A user's subscription to (watch/follow) a document (ADR "Document subscriptions"). While subscribed, the
// user is notified when the document changes — a new confirmed version, a new comment/reply, or the approval
// workflow reaching Released. ITenantScoped, so the tenant query filter applies; append/remove only (not
// versioned/soft-deletable/IConcurrencyTracked). One row per (user, document), enforced by a unique index.
public class DocumentSubscription : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The follower — subscriptions are per-User (a ServiceAccount has no in-app intray to notify).
    public Guid UserId { get; set; }

    public Guid DocumentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
