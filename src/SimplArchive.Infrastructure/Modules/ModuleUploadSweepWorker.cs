using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Deletes module uploads never filed or discarded (ABI 1.12, ADR 0910): anything under a tenant's
/// <c>module-uploads/</c> older than <see cref="MaxAge"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no row to sweep by (the object is the state), so this lists the area per tenant. A presigned PUT is valid
/// for at most an hour and a module files within its request, so a day leaves a wide margin.
/// </para>
/// <para>
/// <b>No claim, deliberately</b> (ADR 0836 asks a sweep to claim before acting). The only act is deleting an object,
/// which is idempotent: both instances deleting the same expired upload is one deletion and a no-op, with nothing
/// sent, written or counted twice.
/// </para>
/// </remarks>
public sealed class ModuleUploadSweepWorker(IServiceScopeFactory scopeFactory, ILogger<ModuleUploadSweepWorker> logger) : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "The module-upload sweep failed; it retries in {Interval}.", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>One pass; answers how many expired uploads it deleted.</summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var tenants = await scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>().Tenants
            .IgnoreQueryFilters(["TenantFilter"]).Select(t => t.Id).ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var tenantId in tenants)
        {
            foreach (var stale in (await storage.ListObjectsAsync(ObjectKeyPrefixes.ModuleUploads(tenantId), cancellationToken))
                .Where(o => o.LastModified < now - MaxAge))
            {
                await storage.DeleteObjectAsync(stale.Key, cancellationToken);
                deleted++;
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} module uploads never filed within {MaxAge}.", deleted, MaxAge);
        }

        return deleted;
    }
}
