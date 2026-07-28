using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A message in a Document's comment/chat thread — see ADR "Document comment thread". The
// thread is append-only for now (no edit/delete). Author is a User or a ServiceAccount, exactly one, the
// same pattern as Document/DocumentVersion.CreatedBy*.
public class DocumentComment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // null = a top-level comment; set = a reply to that comment. One level only — the target must itself be
    // a top-level comment (enforced at POST), so the thread stays two levels deep at most.
    public Guid? ParentCommentId { get; set; }

    public required string Body { get; set; }

    // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set.
    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
