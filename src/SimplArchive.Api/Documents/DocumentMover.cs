using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The one place that knows what crossing the ephemeral/archive boundary costs (#633, ADR 0628).
/// </summary>
/// <remarks>
/// <para>
/// A message delivered by LMTP lives under <c>tenants/{t}/users/{u}/mail/</c>, outside the archive's retention
/// and disposition rules — that is the point of an inbox. Filing it out is therefore not a re-parent: the
/// <b>bytes have to move too</b>, or the archive ends up holding documents whose content sits in storage the
/// sweep is entitled to empty. Correct until the sweep runs, and then silently unreadable, which is the worst
/// shape a bug can have.
/// </para>
/// <para>
/// It exists as a service because <b>seven</b> code paths change a document's parent — the workbench move and
/// its bulk twin, WebDAV, IMAP <c>MOVE</c>, and restore-from-recycle-bin. A fix in the IMAP path alone would
/// have left five silent holes, and each of those callers keeps its own concurrency token, audit line and error
/// translation, so the seam is deliberately the *content* half of a move rather than the whole of it.
/// </para>
/// <para>
/// <b>It does not delete the ephemeral object</b>, and that is what makes it safe to ship before the sweep.
/// Deleting the source would have to happen after the caller's <c>SaveChanges</c> — a second phase, whose
/// failure mode is a row pointing at bytes that are gone. Leaving it costs a duplicate under a prefix whose
/// whole purpose is to be emptied, and an object there that no version references is exactly what the sweep is
/// for. So the ordering is copy → caller saves, never delete → save.
/// </para>
/// <para>
/// Derived artifacts (a message's preview rendition) are deliberately left behind too: they are regenerated on
/// demand and cached, so a stale one costs a regeneration rather than correctness, and enumerating them per
/// version would buy nothing durable.
/// </para>
/// </remarks>
public sealed class DocumentMover(
    SimplArchiveDbContext dbContext,
    IObjectStorageClient storage,
    ILogger<DocumentMover> logger)
{
    /// <summary>
    /// Moves a document's CONTENT for a pending re-parent, and refuses the crossing that is not allowed.
    /// Call it immediately before setting <c>ParentId</c> and saving; the key rewrites ride on the caller's
    /// own <c>SaveChanges</c>, so a refused or failed save leaves the document addressing its original bytes.
    /// </summary>
    /// <returns>True when content was relocated — for the caller's log line; false is the ordinary case.</returns>
    public async Task<bool> RelocateContentForMoveAsync(Guid documentId, Guid? targetParentId, CancellationToken cancellationToken)
    {
        // Every version, not just the current one: a message edited over IMAP has several, and a version left
        // on an ephemeral key is a version that stops resolving the day the prefix is swept.
        var versions = await dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId)
            .ToListAsync(cancellationToken);

        var ephemeral = versions.Where(v => ObjectKeyBuilder.IsEphemeralMailKey(v.ObjectKey)).ToList();
        var targetIsEphemeral = await IsEphemeralFolderAsync(targetParentId, cancellationToken);

        if (targetIsEphemeral)
        {
            // Already there — a move BETWEEN ephemeral folders, or a no-op — is fine; what is refused is
            // carrying archive content back in. Asked of the CONTENT rather than of the source folder, because
            // the content is what would end up somewhere the sweep can reach.
            if (versions.Count > 0 && ephemeral.Count != versions.Count)
            {
                // Warning, not Debug: the caller DOES get a 409 it can render, but an administrator watching a
                // migration or a bulk move wants to know a placement was refused and why — and the reason is a
                // storage-tier rule no client can see (ADR 0626). Trace carries the per-version keys that show
                // which content was already in the archive.
                logger.LogWarning(
                    "Refused to move document {DocumentId} into ephemeral mail folder {TargetFolderId}: {ArchivedCount} of "
                    + "{VersionCount} versions already hold archive content, and filing out of mail storage is one-way. "
                    + "Enable Trace on {Source} for the per-version keys.",
                    documentId, targetParentId, versions.Count - ephemeral.Count, versions.Count, nameof(DocumentMover));

                throw new CannotFileIntoEphemeralMailException();
            }

            logger.LogTrace(
                "Document {DocumentId} moves within ephemeral mail storage to folder {TargetFolderId}; no re-key needed",
                documentId, targetParentId);
            return false;
        }

        if (ephemeral.Count == 0)
        {
            return false;
        }

        var document = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.StorageFolderId })
            .FirstAsync(cancellationToken);

        foreach (var version in ephemeral)
        {
            var archiveKey = ObjectKeyBuilder.Build(
                document.TenantId,
                version.CreatedAt,
                document.StorageFolderId,
                version.Id,
                ExtensionOf(version.ObjectKey));

            // Object first, row second — the same ordering LmtpDelivery uses, for the same reason: bytes with
            // no row are an orphan a sweep finds, a row pointing at absent bytes is a document that opens to
            // an error, and only one of those is recoverable.
            // The whole exchange with object storage, recoverable without a debugger (ADR 0626). Keys are not
            // secrets — a presigned URL's query string is, and none appears here.
            logger.LogTrace(
                "Copying version {VersionId} of document {DocumentId} out of ephemeral mail storage: {SourceKey} → {DestinationKey}",
                version.Id, documentId, version.ObjectKey, archiveKey);

            await storage.CopyObjectAsync(version.ObjectKey, archiveKey, cancellationToken);
            version.ObjectKey = archiveKey;
        }

        // One line per completed move, not per version — a document leaving the inbox for the archive is a
        // lifecycle milestone (it acquires retention, disposition and WORM from here), and the count is what an
        // administrator reconciling storage wants. The ephemeral copies stay until the sweep, deliberately, so
        // say so rather than letting the duplicate look like a leak.
        logger.LogInformation(
            "Filed document {DocumentId} out of ephemeral mail storage into folder {TargetFolderId}: {VersionCount} "
            + "version(s) re-keyed onto archive storage; the ephemeral copies remain until the sweep removes them",
            documentId, targetParentId, ephemeral.Count);

        return true;
    }

    /// <summary>Whether a folder is ephemeral mail storage — it wears the <c>IMAP Special</c> mask.</summary>
    private async Task<bool> IsEphemeralFolderAsync(Guid? folderId, CancellationToken cancellationToken)
    {
        if (folderId is not { } id)
        {
            return false; // a repository root is never ephemeral
        }

        var maskId = await dbContext.Documents
            .Where(d => d.Id == id)
            .Select(d => dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);

        return maskId == WellKnownMaskIds.ImapSpecial;
    }

    /// <summary>The extension of a key's leaf, so the archive key keeps the type the download relies on.</summary>
    private static string? ExtensionOf(string objectKey)
    {
        var leaf = objectKey[(objectKey.LastIndexOf('/') + 1)..];
        var dot = leaf.LastIndexOf('.');
        return dot < 0 ? null : leaf[dot..];
    }
}
