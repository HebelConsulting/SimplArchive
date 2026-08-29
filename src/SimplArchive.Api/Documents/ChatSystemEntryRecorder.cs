using Microsoft.Extensions.DependencyInjection;
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
    private readonly TimeProvider _clock;

    // The keyed "demo-clock" — a FIXED instant only when Demo:Clock is set (the manual capture and the demo
    // seed), TimeProvider.System everywhere else, exactly as AuditRecorder resolves it. A system entry is
    // demo-visible content, so it has to date from the same clock the rest of the demo does.
    public ChatSystemEntryRecorder(SimplArchiveDbContext dbContext, [FromKeyedServices("demo-clock")] TimeProvider clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    // Called once a version is confirmed — ONE entry per version, first or not. Filing used to add a second,
    // separate "filed a new document" entry beside it, which said the same thing twice and left the per-version
    // one reading "saved a new working version" of a document that had no earlier version. The version number
    // the entry already points at is enough for a client to choose the right sentence, so the split earned
    // nothing. Which version this is stays the clients' question to answer, not a fact duplicated here.
    // Dated from the VERSION, not from "now": this entry records that filing, so the two must agree. They did
    // not — the entry took DateTimeOffset.UtcNow while the version kept its own CreatedAt, so a demo document
    // filed in June carried a feed entry stamped today, and the manual's chat screenshots changed on every
    // capture run because the timestamp was literally the moment of capture (issue #478).
    public async Task RecordVersionFiledAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        Add(version.TenantId, version.DocumentId, ChatMessageKind.VersionFiled, version.Id,
            version.CreatedByUserId, version.CreatedByServiceAccountId, version.CreatedAt);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // Called when an older version is pinned as current. The author is the person doing the pinning, NOT whoever
    // uploaded that version originally — the feed records who changed what everyone sees, and those are usually
    // different people.
    public async Task RecordVersionActivatedAsync(
        Guid tenantId, Guid documentId, Guid documentVersionId, Guid? userId, Guid? serviceAccountId,
        CancellationToken cancellationToken)
    {
        // The pinning is happening NOW (unlike a filing, which is dated from its version), so it takes the clock.
        Add(tenantId, documentId, ChatMessageKind.VersionActivated, documentVersionId, userId, serviceAccountId, _clock.GetUtcNow());
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // Called when an email's attachment was refused by the upload content policy (ADR 0718). The BODY carries
    // the attachment's file name — the one datum a client cannot compose — while the sentence around it stays
    // in the clients' resources, like every other system entry. Dated from the email's own filing, so the note
    // sits with the version it belongs to rather than at whatever moment the extraction happened to run.
    public async Task RecordAttachmentRefusedAsync(
        DocumentVersion emailVersion, string attachmentFileName, CancellationToken cancellationToken)
    {
        _dbContext.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = emailVersion.TenantId,
            DocumentId = emailVersion.DocumentId,
            Kind = ChatMessageKind.AttachmentRefused,
            DocumentVersionId = null,
            Body = attachmentFileName,
            CreatedByUserId = emailVersion.CreatedByUserId,
            CreatedByServiceAccountId = emailVersion.CreatedByServiceAccountId,
            CreatedAt = emailVersion.CreatedAt,
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void Add(Guid tenantId, Guid documentId, ChatMessageKind kind, Guid? documentVersionId, Guid? userId, Guid? serviceAccountId, DateTimeOffset at) =>
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
            CreatedAt = at,
        });
}
