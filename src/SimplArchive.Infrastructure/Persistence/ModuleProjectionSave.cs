using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// The document write and every module projection of it commit together or not at all (ADR 0848).
/// </summary>
/// <remarks>
/// <para>
/// A deliberate divergence from the two hooks beside it in <c>SaveChangesAsync</c> — the realtime pusher and
/// the DAV recorder both act AFTER the save has committed. Those can afford to: a missed push is a stale pane
/// somebody refreshes. A missed projection leaves a REVOKED CERTIFICATE ADDRESSED, and there is no acceptable
/// window for that, so this one shares the transaction.
/// </para>
/// <para>
/// A module's exception is NOT caught, which inverts how the core treats a throwing module everywhere else
/// (<c>ModuleReaderCertificates</c> swallows one and answers empty). The asymmetry is the DIRECTION of the
/// failure: a refused read is safe and the reader is told, whereas a projection that silently did not happen
/// is discovered by nobody.
/// </para>
/// </remarks>
internal static class ModuleProjectionSave
{
    /// <summary>
    /// Saves, then projects, inside one transaction.
    /// </summary>
    /// <param name="save">The context's own <c>base.SaveChangesAsync</c> — passed in rather than called here,
    /// because only the context can reach its base implementation and calling the public one would recurse.</param>
    internal static async Task<int> SaveAsync(
        SimplArchiveDbContext db,
        Func<CancellationToken, Task<int>> save,
        IModuleProjectionDispatcher? dispatcher,
        CancellationToken cancellationToken)
    {
        // COLLECTED HERE, before the save, and it has to be: the ChangeTracker states are gone once
        // SaveChangesAsync returns, so anything read afterwards describes nothing. The ids are what survive.
        var projections = await ModuleProjectionCollector.CollectAsync(
            db, dispatcher?.ProjectedMasks ?? [], cancellationToken);

        // NOTHING TO PROJECT is the overwhelmingly common case — no module declares a mask, or this save
        // touched none of them — and it must be byte-for-byte what it was before 1.1: no transaction is
        // opened for a save with no projection to keep in step with.
        if (projections.Count == 0 || dispatcher is null)
        {
            return await save(cancellationToken);
        }

        // An AMBIENT transaction means a caller (the module engine, ADR 0737) already owns the boundary, so
        // opening a second one here would be an error rather than a nesting. Join theirs instead.
        if (db.Database.CurrentTransaction is { } ambient)
        {
            var inner = await save(cancellationToken);
            await DispatchAsync(db, ambient, dispatcher, projections, cancellationToken);
            return inner;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var saved = await save(cancellationToken);
        await DispatchAsync(db, transaction, dispatcher, projections, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    /// <summary>
    /// Dispatches every collected projection ON THIS SAVE'S connection and transaction.
    /// </summary>
    /// <remarks>
    /// Lending both is not a convenience — the rows this save wrote are not committed yet, so a projection on
    /// any other connection cannot see the document it was called about, and a projection's own write on
    /// another connection would commit independently of the document it describes. See
    /// <see cref="AmbientDatabaseTransaction"/> for what that cost in practice.
    /// </remarks>
    private static async Task DispatchAsync(
        SimplArchiveDbContext db,
        IDbContextTransaction transaction,
        IModuleProjectionDispatcher? dispatcher,
        List<ModuleProjectionCollector.Change> projections,
        CancellationToken cancellationToken)
    {
        if (dispatcher is null)
        {
            return;
        }

        var ambient = transaction.GetDbTransaction() is { } dbTransaction
            ? new AmbientDatabaseTransaction(db.Database.GetDbConnection(), dbTransaction)
            : null;

        foreach (var change in projections)
        {
            await dispatcher.DispatchAsync(
                change.DocumentId, change.MaskId, change.TenantId, change.Removed, ambient, cancellationToken);
        }
    }
}
