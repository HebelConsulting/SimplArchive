using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Imap;

// One IMAP mailbox's identity state (ADR "IMAP endpoint (read-only, first slice)"). Every mailbox IS a folder
// Document (INBOX = the personal repository root, shared repositories and their subfolders), so the folder's id
// keys the row. Created lazily the first time a mailbox is SELECTed.
//
// UidValidity + NextUid implement RFC 3501's UID contract: UIDs are 32-bit, strictly ascending per mailbox,
// NEVER reused, and clients cache messages by (UIDVALIDITY, UID) across sessions — which is why this is a
// table and not an in-memory counter that a restart would reset (a reset would silently corrupt every
// connected client's cache).
public class ImapMailbox : ITenantScoped
{
    // The mailbox's folder Document. Also the primary key — one row per folder.
    public Guid FolderId { get; set; }

    public Guid TenantId { get; set; }

    // Bumped only when the mailbox's identity changes so drastically that cached UIDs are meaningless
    // (per RFC 3501 §2.3.1.1); stable otherwise.
    public int UidValidity { get; set; }

    // The next UID to hand out (RFC 3501 UIDNEXT) — monotonically increasing, never rewound.
    public int NextUid { get; set; }
}
