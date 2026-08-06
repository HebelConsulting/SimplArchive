using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

// Writes the automatic entries in a document's chat thread (ADR 0545): a document was filed, a version was saved,
// an older version was made current again.
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

    // Called once a version is confirmed. A document's FIRST version produces two entries — "filed a new
    // document" plus its "Version 1" entry — so that every version, including the first, has the same per-version
    // entry in the feed, and the arrival of the document itself is still announced separately.
    public async Task RecordVersionFiledAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        var isFirstVersion = version.VersionNumber is null or <= 1;

        if (isFirstVersion)
        {
            Add(version.TenantId, version.DocumentId, ChatMessageKind.DocumentFiled, documentVersionId: null,
                version.CreatedByUserId, version.CreatedByServiceAccountId);
        }

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
