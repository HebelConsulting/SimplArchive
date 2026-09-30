using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The two questions every version-scoped door asks: <i>which version is this?</i> and <i>may the caller see
/// its content?</i>
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>DocumentVersionsController</c> (ADR 0861), the same recipe and for the same reason as
/// <see cref="DocumentAccessService"/> before it (ADR 0571, issue #466): these were private to that
/// controller, so a sibling controller on the same routes could not be written without copying them — and a
/// copy is how the fourth caller gets a fix the first three do not.
/// </para>
/// <para>
/// The controller is what forced it. It stood at 989 lines with ten of headroom, so the gate ADR 0861 adds
/// could not fit, and the honest answer to the 1000-line rule is a responsibility split rather than an
/// exception granted to the file that happened to be next.
/// </para>
/// </remarks>
public sealed class DocumentVersionAccess(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    ICurrentUserAccessor currentUserAccessor)
{
    /// <summary>One version of one document, or null when either does not exist.</summary>
    /// <remarks>
    /// Serves a SOFT-DELETED document's versions on purpose (ADR "Recycle bin tab"): the detail pane previews
    /// a recycle-bin item, and with the filter on, this matched zero rows. Only "SoftDeleteFilter" is named,
    /// so tenant isolation stays enforced.
    /// </remarks>
    public async Task<VersionRow?> LoadForReadAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
            .AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return null;
        }

        var version = await dbContext.DocumentVersions
            .Where(v => v.Id == versionId && v.DocumentId == documentId)
            .Select(v => new { v.Status, v.VersionNumber, v.ObjectKey, v.Sha256Hash, v.CreatedAt, v.DocumentDate, v.DocumentTime, v.CreatedByUserId, v.CreatedByServiceAccountId, v.OcrLanguages, v.Comment, v.OcrVerdict, v.IsSigned })
            .SingleOrDefaultAsync(cancellationToken);

        return version is null
            ? null
            : new VersionRow(versionId, documentId, version.Status, version.VersionNumber, version.ObjectKey,
                version.Sha256Hash, version.CreatedAt, version.DocumentDate, version.DocumentTime,
                version.CreatedByUserId, version.CreatedByServiceAccountId, version.OcrLanguages,
                version.Comment, version.OcrVerdict, version.IsSigned);
    }

    /// <summary>
    /// Content access for a SPECIFIC version (ADR "Workflow status-gating").
    /// </summary>
    /// <remarks>
    /// Requires <c>CanReadContent</c>, and — where the version is *gated* (it entered a workflow and is not
    /// yet Released) — also <c>CanEditContent</c> (editors / tenant admins) or being that version's assigned
    /// reviewer. A never-submitted version (no <c>WorkflowState</c>) and a Released one are ungated, so only
    /// in-workflow, not-yet-Released versions are restricted.
    /// </remarks>
    public async Task<bool> CanAccessVersionContentAsync(Guid versionId, Guid documentId, CancellationToken cancellationToken)
    {
        var rights = await access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return false;
        }

        if (rights.CanEditContent)
        {
            return true; // editors / admins see every version
        }

        var state = await dbContext.WorkflowStates
            .FirstOrDefaultAsync(w => w.DocumentVersionId == versionId, cancellationToken);
        if (state is null || state.Status == WorkflowStatus.Released)
        {
            return true; // ungated
        }

        return currentUserAccessor.UserId is { } userId && state.AssignedToUserId == userId; // the assigned reviewer
    }
}
