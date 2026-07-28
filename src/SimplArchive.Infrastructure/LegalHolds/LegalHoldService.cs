using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.LegalHolds;

// Determines whether a document is frozen by an active legal hold (ADR "Legal hold & retention enforcement").
// A document is frozen if it — or any ancestor — is covered by a hold whose ReleasedAt is null. The ancestor
// walk mirrors ACL inheritance (one query per level; a single ancestor chain, so no bulk load). Scoped; runs in
// the request's tenant context, so the tenant + soft-delete query filters apply as normal.
public sealed class LegalHoldService : ILegalHoldService
{
    private readonly SimplArchiveDbContext _dbContext;

    public LegalHoldService(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsFrozenAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        // Collect the document + its ancestor chain, walking up ParentId.
        var chain = new List<Guid>();
        Guid? current = documentId;
        while (current is { } id)
        {
            chain.Add(id);
            current = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => d.ParentId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await AnyDirectlyHeldAsync(chain, cancellationToken);
    }

    public async Task<bool> AnyDirectlyHeldAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return false;
        }

        return await (
            from item in _dbContext.LegalHoldItems
            where documentIds.Contains(item.DocumentId)
            join hold in _dbContext.LegalHolds on item.LegalHoldId equals hold.Id
            where hold.ReleasedAt == null
            select item.Id).AnyAsync(cancellationToken);
    }
}
