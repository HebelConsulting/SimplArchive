using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Purges module-staged ephemeral content (ABI 0.6): every document whose <c>ExpiresAt</c> has passed, and the
/// <c>fs/special/</c> objects its versions reference. Distinct from the ephemeral MAIL sweep — the signal here
/// is the explicit per-document expiry a module stamped through <c>StageContentAsync</c> (a DABS chart's
/// validity date, a METAR's short TTL), not a folder-name-plus-window rule. Permanent module reference data
/// (<c>CreateContentDocumentAsync</c>, ExpiresAt null) is never touched.
/// </summary>
public sealed class EphemeralContentSweepWorker : BackgroundService
{
    // Long enough not to sweep while the app is still opening connections; short enough that a demo need not
    // wait a working day to watch it happen.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EphemeralContentSweepWorker> _logger;
    private readonly TimeSpan _interval;

    public EphemeralContentSweepWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<EphemeralContentSweepWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var hours = configuration.GetValue<int?>("EphemeralContent:SweepIntervalHours") is { } h && h > 0 ? h : 6;
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Worker} started (every {Interval}, purging staged content past its ExpiresAt).",
            nameof(EphemeralContentSweepWorker), _interval);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>One pass; returns the number purged. Public so a test can drive it directly.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

            var now = DateTimeOffset.UtcNow;

            // Host-wide, filters off (like the mail/retention/audit sweeps): no current tenant, and a
            // soft-deleted staged item is doubly gone, not exempt. The null-check is the SQL predicate (the
            // partial index makes it cheap); the expiry comparison is in memory, because SQLite cannot compare
            // a DateTimeOffset in SQL (provider parity — the model runs on both) and the staged set is small.
            var expired = (await db.Documents.IgnoreQueryFilters()
                    .Where(d => d.ExpiresAt != null)
                    .Select(d => new { d.Id, d.ExpiresAt })
                    .ToListAsync(cancellationToken))
                .Where(d => d.ExpiresAt < now)
                .Select(d => d.Id)
                .ToList();

            var swept = 0;
            foreach (var documentId in expired)
            {
                var keys = await db.DocumentVersions.IgnoreQueryFilters()
                    .Where(v => v.DocumentId == documentId)
                    .Select(v => v.ObjectKey)
                    .ToListAsync(cancellationToken);

                var document = await db.Documents.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
                if (document is null)
                {
                    continue;
                }

                // Rows first, objects second (the mail sweep's reasoning): a row pointing at absent bytes opens
                // to an error, while an object with no row is merely reclaimable space.
                db.Documents.Remove(document);
                await db.SaveChangesAsync(cancellationToken);

                foreach (var key in keys)
                {
                    _logger.LogTrace(
                        "Ephemeral content sweep: deleting {ObjectKey} for expired staged document {DocumentId}", key, documentId);
                    await storage.DeleteObjectAsync(key, cancellationToken);
                }

                swept++;
            }

            if (swept > 0)
            {
                _logger.LogInformation("Ephemeral content sweep: purged {Swept} staged document(s) past their expiry.", swept);
            }

            return swept;
        }
        catch (Exception e)
        {
            // A sweep that throws must not take the host down, and must not go quiet: the next pass retries.
            _logger.LogError(e, "Ephemeral content sweep failed; the next pass will retry.");
            return 0;
        }
    }
}
