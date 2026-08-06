using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A "reference": a shortcut that files an existing Document (a leaf document or a folder)
// into another folder, without changing the target's real home location (Document.ParentId). See ADR
// "Desktop drag-and-drop move and reference". The target keeps its real parent; a reference is just an
// extra appearance of it inside ParentFolderId. Append/remove only — not versioned, soft-deletable, or
// concurrency-tracked (same as ChatMessage). A reference whose target is soft-deleted is filtered out
// of listings and reappears on restore. Author is a User or a ServiceAccount, exactly one.
public class DocumentReference : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The folder the shortcut sits in.
    public Guid ParentFolderId { get; set; }

    // The referenced document/folder (its real ParentId is unchanged).
    public Guid TargetDocumentId { get; set; }

    // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set.
    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
