using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Checkouts;

/// <summary>
/// Resolves a check-out THIS caller holds — the stash key + the current confirmed version — or the refusal.
/// Extracted, not copied, when the working-copy page operations became a second controller needing the same
/// holder-only rule (the <c>InboxScopeResolver</c> precedent, ADR 0575): an authorization rule with two
/// implementations is one that gets tightened in only one of them.
/// </summary>
public static class HeldCheckout
{
    public enum Refusal
    {
        None,

        /// <summary>No principal, or the caller is not the lock holder — only the holder may touch their own working copy.</summary>
        Forbidden,

        /// <summary>The document does not exist (for this tenant).</summary>
        NotFound,
    }

    public sealed record Result(string StashKey, DocumentVersion? Version, Refusal Refusal);

    public static async Task<Result> ResolveAsync(
        SimplArchiveDbContext dbContext,
        Guid? userId,
        Guid? tenantId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (userId is not { } uid || tenantId is not { } tid)
        {
            return new(string.Empty, null, Refusal.Forbidden);
        }

        var document = await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return new(string.Empty, null, Refusal.NotFound);
        }

        if (document.CheckedOutByUserId != uid)
        {
            return new(string.Empty, null, Refusal.Forbidden);
        }

        var version = await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, documentId, document.CurrentVersionId, cancellationToken);
        return new(CheckoutStashKey.Build(tid, uid, documentId), version, Refusal.None);
    }
}
