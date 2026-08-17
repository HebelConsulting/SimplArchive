using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Imap;

// A document's stable IMAP UID within one mailbox (ADR "IMAP endpoint (read-only, first slice)"). Assigned
// lazily from <see cref="ImapMailbox.NextUid"/> the first time the document is listed in that mailbox, and
// never changed or reused afterwards — clients cache by UID, so reassignment corrupts their view. A document
// referenced into several folders gets an independent UID per mailbox (UIDs are a per-mailbox concept).
// Composite PK (FolderId, DocumentId); unique (FolderId, Uid).
public class ImapMessageUid : ITenantScoped
{
    public Guid FolderId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    public int Uid { get; set; }
}
