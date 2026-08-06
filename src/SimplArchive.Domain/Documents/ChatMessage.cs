using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A message in a Document's comment/chat thread — see ADR "Document comment thread". The
// thread is append-only for now (no edit/delete). Author is a User or a ServiceAccount, exactly one, the
// same pattern as Document/DocumentVersion.CreatedBy*.
public class ChatMessage : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // null = a top-level comment; set = a reply to that comment. One level only — the target must itself be
    // a top-level comment (enforced at POST), so the thread stays two levels deep at most.
    public Guid? ParentMessageId { get; set; }

    // What produced this message. A UserPost is somebody typing; the rest are recorded automatically when a
    // document is filed, a version is saved, or an older version is made current again (ADR 0545).
    public ChatMessageKind Kind { get; set; }

    // The version a system message is about — set for VersionFiled/VersionActivated, null otherwise (enforced by
    // a check constraint). The entry's "Version N" label and any check-in comment are read from HERE at render
    // time rather than copied into Body, so editing a version's comment can never leave a stale copy in the feed.
    public Guid? DocumentVersionId { get; set; }

    // The message text. Empty for every system kind: their wording is a LOCALIZED TEMPLATE the clients render
    // (with the author as a slot, so the name stays a clickable card), not English frozen into the database.
    public required string Body { get; set; }

    // Exactly one of CreatedByUserId/CreatedByServiceAccountId is set.
    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByServiceAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

// What produced a ChatMessage (ADR 0545). Only UserPost carries text of its own; the system kinds render from a
// localized template, which is why their wording is not stored.
public enum ChatMessageKind
{
    // Somebody typed it. The only kind a client can create.
    UserPost = 0,

    // A document was filed for the first time — its first version was confirmed.
    DocumentFiled = 1,

    // A new version was saved. Also emitted for a brand-new document's Version 1, alongside DocumentFiled, so
    // the feed shows the same per-version entry for every version a document ever has.
    VersionFiled = 2,

    // An older version was made current again. Worth recording precisely because it changes what everyone else
    // sees as the document without adding anything to it.
    VersionActivated = 3,
}
