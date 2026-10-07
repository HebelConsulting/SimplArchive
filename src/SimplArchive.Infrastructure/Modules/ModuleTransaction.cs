using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The ONE way module work runs atomically (ADRs 0737/0738, 0902): a transition's handler, and since ABI 1.10 a module's
/// own <c>InTransactionAsync</c>. Every wired module read-model context shares the core connection and is enlisted, so a
/// document write and a projection write are one commit and one rollback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Joins a transaction already in flight</b> rather than beginning a second one, which EF refuses on one connection:
/// a module's <c>InTransactionAsync</c> may call the engine, and a transition handler may call
/// <c>InTransactionAsync</c>. The outermost owner commits, or rolls back and clears the trackers.
/// </para>
/// <para>
/// On a throw the change TRACKERS are cleared as well: the database rolls back on dispose, but the contexts would still
/// hold the writes as clean entities, so a later FindAsync would serve a phantom row and a later save could resurrect
/// rolled-back state (found by the rollback test reading Count = 1 from a table that held nothing).
/// </para>
/// </remarks>
public static class ModuleTransaction
{
    public static async Task<T> RunAsync<T>(
        SimplArchiveDbContext dbContext, ModuleReadModelCatalog readModels, IServiceProvider services,
        Func<Task<T>> body, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await body();   // joined: the outermost owner commits or rolls back
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var enlisted = new List<DbContext>();
        foreach (var contextType in readModels.ContextTypes)
        {
            var readModelContext = (DbContext)services.GetRequiredService(contextType);
            await readModelContext.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
            enlisted.Add(readModelContext);
        }

        try
        {
            var result = await body();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            foreach (var readModelContext in enlisted)
            {
                readModelContext.ChangeTracker.Clear();
            }

            throw;
        }
        finally
        {
            foreach (var readModelContext in enlisted)
            {
                await readModelContext.Database.UseTransactionAsync(null, CancellationToken.None);
            }
        }
    }
}
