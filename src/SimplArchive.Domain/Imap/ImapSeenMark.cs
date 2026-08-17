using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Imap;

// A user's \Seen mark on a document (ADR "IMAP endpoint: persisted read state", #562 slice 2). Per USER and
// per DOCUMENT — not per mailbox: a document referenced into two folders reads as seen in both, which is what
// a person expects of the same document. Row present = seen; absence = unseen — the flag IS the row.
public class ImapSeenMark : ITenantScoped
{
    public Guid UserId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset SeenAt { get; set; }
}
