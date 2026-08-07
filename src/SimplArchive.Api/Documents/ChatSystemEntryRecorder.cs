using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// Writes the automatic entries in a document's chat thread (ADR 0545): a version was confirmed (which for a
// first version IS the document being filed), or an older version was made current again.
//
// These carry NO text. Their wording is a localized template each client renders, with the author as a slot so
// the name stays a clickable identity card (ADR 0544) — storing an English sentence would freeze it for every
// German, Italian and Spanish reader. The version's number and check-in comment are likewise read from
// DocumentVersionId at render time, never copied here, so editing a comment can't leave a stale copy in the feed.
//
// Deliberately narrow: this is called only from the paths where a PERSON files something — DocumentFinalizer,
// which every interactive upload funnels through, and the version-restore action. A repository import, the demo
// seeder and the searchable-PDF worker all create confirmed versions without it, and stay silent: a bulk import
// would otherwise flood every document's thread on day one, and an OCR successor isn't something anyone did.
public sealed class ChatSystemEntryRecorder
{
    private readonly SimplArchiveDbContext _dbContext;

    public ChatSystemEntryRecorder(SimplArchiveDbContext dbContext) => _dbContext = dbContext;

    // Called once a version is confirmed — ONE entry per version, first or not. Filing used to add a second,
    // separate "filed a new document" entry beside it, which said the same thing twice and left the per-version
    // one reading "saved a new working version" of a document that had no earlier version. The version number
    // the entry already points at is enough for a client to choose the right sentence, so the split earned
    // nothing. Which version this is stays the clients' question to answer, not a fact duplicated here.
    public async Task RecordVersionFiledAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        Add(version.TenantId, version.DocumentId, ChatMessageKind.VersionFiled, version.Id,
            version.CreatedByUserId, version.CreatedByServiceAccountId);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // Called when an older version is pinned as current. The author is the person doing the pinning, NOT whoever
    // uploaded that version originally — the feed records who changed what everyone sees, and those are usually
    // different people.
    public async Task RecordVersionActivatedAsync(
        Guid tenantId, Guid documentId, Guid documentVersionId, Guid? userId, Guid? serviceAccountId,
        CancellationToken cancellationToken)
    {
        Add(tenantId, documentId, ChatMessageKind.VersionActivated, documentVersionId, userId, serviceAccountId);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void Add(Guid tenantId, Guid documentId, ChatMessageKind kind, Guid? documentVersionId, Guid? userId, Guid? serviceAccountId) =>
        _dbContext.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Kind = kind,
            DocumentVersionId = documentVersionId,
            // Empty, not null: Body is required, and a system entry's words live in the clients' resources.
            Body = "",
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
}
