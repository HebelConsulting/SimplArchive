namespace SimplArchive.Api.Imap;

// RFC 8474 OBJECTID: the stable, server-assigned ids a client needs to tell "the same thing, seen again" from
// "a different thing that looks alike" (#780). Everything here is a pure rendering of an id the archive
// already holds — there is no id store, and nothing to keep in sync.
//
// WHY IT IS NEEDED AT ALL, given IMAP already has UID and UIDVALIDITY: neither is an identity.
//   * A UID is scoped to ONE mailbox, so a document reached through its home folder and through a folder it is
//     referenced into carries TWO UIDs, and nothing on the wire says they are one document.
//   * A mailbox is addressed by NAME, so renaming a folder in the workbench reads to a client as the old
//     mailbox being deleted and a new one appearing — it re-downloads the lot.
//   * UIDVALIDITY is a cache-invalidation counter, not a name: it says "forget what you knew", not "who this is".
//
// The ids are the archive's own GUIDs in "N" form (32 lowercase hex, no braces or dashes), which is inside the
// charset RFC 8474 §3 mandates for an ObjectID (1–255 of a-z A-Z 0-9 _ -). Rendering an id we already have,
// rather than minting and storing a new one, is what makes every invariant below hold for free.
internal static class ImapObjectId
{
    // MAILBOXID = the folder's id. RFC 8474 §5 demands two things a name-derived id could not give:
    //   * "The server MUST keep the same MAILBOXID for the source and destination when renaming a mailbox" —
    //     holds because a rename changes Document.Name and never Document.Id.
    //   * "MUST NOT reuse the same MAILBOXID" for a delete-then-recreate of the same name — holds because the
    //     recreated folder is a new Document with a fresh GUID.
    internal static string ForMailbox(Guid folderId) => folderId.ToString("N");

    // EMAILID = the DOCUMENT's id, not the (folder, document) appearance. This is not a preference: RFC 8474 §4
    // requires that "the server MUST return the same EMAILID as the source message for the matching destination
    // message ... after a COPY or MOVE command", and our IMAP COPY files a DocumentReference (ImapWrites
    // .MoveOrCopyAsync) rather than duplicating bytes. So a referenced document IS the RFC's copy, and the two
    // appearances must agree — which is also the archive's own position on a reference (it is the same row).
    //
    // The consequence worth stating: two SEPARATE archivings of one mail — your copy and a colleague's, filed
    // independently — are two documents and so two EMAILIDs, even where the Message-ID matches. That is the
    // decided semantics (#780), and it is the honest one: they are two documents with their own versions,
    // rights and audit trail. Message-ID sameness is answered by the duplicates probe (#704) and, within a
    // folder, by the APPEND correlation in ImapWrites — not by pretending one identity where there are two.
    internal static string ForMessage(Guid documentId) => documentId.ToString("N");
}
