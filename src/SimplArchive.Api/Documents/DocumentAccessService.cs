using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The caller-facing access questions every Document-scope controller asks: who is calling, what may they do to
/// this document, which system rights do they hold, and is the document currently untouchable (frozen by a
/// legal hold, or checked out by someone else).
/// </summary>
/// <remarks>
/// Extracted from <c>DocumentsController</c> (issue #466): these helpers were private to it while EIGHT sibling
/// controllers carried their own copies of the same <c>GetCallerRightsAsync</c> — the exact drift CLAUDE.md's
/// "same work across several types is ONE generic implementation" principle names. The three-way principal
/// branch (ServiceAccount first, then User, then no rights) mirrors <c>CurrentPrincipalMiddleware</c>; see ADR
/// 0209 for why the accessors are mutually exclusive per request.
/// </remarks>
public sealed class DocumentAccessService(
    SimplArchiveDbContext dbContext,
    IEffectiveRightsCalculator effectiveRightsCalculator,
    ICurrentServiceAccountAccessor currentServiceAccountAccessor,
    ICurrentUserAccessor currentUserAccessor,
    IUserSystemRightsResolver userSystemRights,
    ILegalHoldService legalHold)
{
    /// <summary>The caller's effective rights on one document — ServiceAccount first, then User, else none.</summary>
    public async Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken);
        }

        if (currentUserAccessor.UserId is { } userId)
        {
            return await effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken);
        }

        return new EffectiveRights(false, false, false, false, false, false, false, false, false);
    }

    /// <summary>The same question for a whole page of documents, in one batch (#858).</summary>
    /// <remarks>
    /// The three-way principal branch is identical to the single-document form above — ServiceAccount first,
    /// then User, else nothing — because a listing must gate on exactly the rights the endpoints enforce, and
    /// a second answer to "who is calling" is how those two come apart.
    ///
    /// The no-principal case returns an entry per id rather than an empty map, so a caller can read every row's
    /// answer without deciding what a MISSING entry meant. That distinction matters: absent and false would
    /// both disable the affordance today, and would stop agreeing the moment someone treated absent as "not
    /// computed, ask again".
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, EffectiveRights>> GetCallerRightsForManyAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await effectiveRightsCalculator.GetEffectiveRightsForManyForServiceAccountAsync(serviceAccountId, documentIds, cancellationToken);
        }

        if (currentUserAccessor.UserId is { } userId)
        {
            return await effectiveRightsCalculator.GetEffectiveRightsForManyAsync(userId, documentIds, cancellationToken);
        }

        var none = new EffectiveRights(false, false, false, false, false, false, false, false, false);
        return documentIds.Distinct().ToDictionary(id => id, _ => none);
    }

    public async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken) =>
        (await GetCallerRightsAsync(documentId, cancellationToken)).CanSee;

    public async Task<bool> CanEditIndexDataAsync(Guid documentId, CancellationToken cancellationToken) =>
        (await GetCallerRightsAsync(documentId, cancellationToken)).CanEditIndexData;

    /// <summary>
    /// Whichever principal actually made this request, for Document/DocumentVersion creator attribution
    /// (CreatedByUserId/CreatedByServiceAccountId, CHECK-constrained to exactly one).
    /// </summary>
    public (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity()
    {
        if (currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (null, serviceAccountId);
        }

        return (currentUserAccessor.UserId, null);
    }

    /// <summary>
    /// Tenant-admin — the bar for permanent destruction (a User right; a ServiceAccount has no IsTenantAdmin).
    /// See ADR "Manual hard-delete / purge".
    /// </summary>
    public async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken)
    {
        if (currentUserAccessor.UserId is { } userId)
        {
            return (await userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
        }

        return false;
    }

    /// <summary>The caller's effective CanExport — User own∪groups, or the ServiceAccount's own column.</summary>
    public Task<bool> HasExportRightAsync(CancellationToken cancellationToken) =>
        HasSystemRightAsync(r => r.CanExport, s => s.CanExport, cancellationToken);

    public Task<bool> HasImportRightAsync(CancellationToken cancellationToken) =>
        HasSystemRightAsync(r => r.CanImport, s => s.CanImport, cancellationToken);

    /// <summary>Gates demoting a repository by moving a root document into a folder (ADR "Repository creation").</summary>
    public Task<bool> HasManageRepositoriesRightAsync(CancellationToken cancellationToken) =>
        HasSystemRightAsync(r => r.CanManageRepositories, s => s.CanManageRepositories, cancellationToken);

    // One implementation for the per-right lookups above — the per-right surface forwards with two lambdas
    // (read the User's effective right; read the ServiceAccount's column), per CLAUDE.md's type-specific-action
    // principle. The ServiceAccount selector is an expression so EF translates the column read.
    private async Task<bool> HasSystemRightAsync(
        Func<SystemRightsSet, bool> userRight,
        System.Linq.Expressions.Expression<Func<Domain.ServiceAccounts.ServiceAccount, bool>> serviceAccountRight,
        CancellationToken cancellationToken)
    {
        if (currentUserAccessor.UserId is { } userId)
        {
            return userRight(await userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken));
        }

        if (currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await dbContext.ServiceAccounts.Where(s => s.Id == serviceAccountId).Select(serviceAccountRight).SingleOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    /// <summary>Refuses a mutation on a document frozen by an active legal hold (ADR "Legal hold enforcement").</summary>
    public async Task EnsureNotFrozenAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (await legalHold.IsFrozenAsync(documentId, cancellationToken))
        {
            throw new DocumentUnderLegalHoldException();
        }
    }

    /// <summary>
    /// Refuses a mutation on a document checked out by a DIFFERENT user — the full edit-lock (ADR "Document
    /// check-out / check-in"). The holder proceeds; a ServiceAccount caller (no UserId) is never the holder,
    /// so any active checkout blocks it.
    /// </summary>
    public async Task EnsureNotCheckedOutByOtherAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var holder = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.CheckedOutByUserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (holder is { } h && h != currentUserAccessor.UserId)
        {
            throw new DocumentCheckedOutException();
        }
    }

    /// <summary>
    /// Refuses a mutation while any of the document's versions is in an unfinished workflow (ADR "Workflow
    /// status-gating"): a version under review is a claim someone is examining, and changing the document
    /// under them invalidates the examination.
    /// </summary>
    public async Task EnsureNoWorkflowInProgressAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var inProgress = await dbContext.WorkflowStates
            .Where(w => (w.Status == Domain.Workflow.WorkflowStatus.InReview || w.Status == Domain.Workflow.WorkflowStatus.Approved)
                && dbContext.DocumentVersions.Any(v => v.Id == w.DocumentVersionId && v.DocumentId == documentId))
            .AnyAsync(cancellationToken);
        if (inProgress)
        {
            throw new Errors.Exceptions.Workflow.WorkflowInProgressException();
        }
    }
}
