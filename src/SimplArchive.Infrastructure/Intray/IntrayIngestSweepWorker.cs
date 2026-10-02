using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// The backstop for the intray ingest pipeline (issue #494): items that arrived with no client to signal it.
/// </summary>
/// <remarks>
/// <para>
/// It exists because <b>the intray is also a WebDAV mount</b> (ADR 0509). A file dropped into the mounted folder
/// never touches the upload endpoint, so the client-signalled path cannot see it — and neither can a browser
/// tab that was closed between the storage PUT and the call that follows it. Without this sweep the automatic
/// straightening would quietly not apply to a whole ingest path, which is the kind of gap a user reports as
/// "it works for some of my scans".
/// </para>
/// <para>
/// <b>It is a backstop, not a migration.</b> Every item already in an intray when this ships has no marker, so a
/// naive sweep would treat the entire existing intray as new and convert every scan in it — a bulk rewrite of
/// files people already have, triggered by nobody pressing anything. So an unmarked item older than
/// <see cref="ArrivalWindow"/> is marked as seen WITHOUT being processed. The window is generous enough to
/// cover a file dropped over a weekend and finished on Monday, and short enough that history is left alone.
/// </para>
/// <para>
/// Own intrays only. A group intray has no user whose preference to read (the setting is per-user, #491), and
/// guessing whose it is would be worse than leaving those to the client-signalled path, where the person who
/// uploaded is known.
/// </para>
/// </remarks>
public sealed class IntrayIngestSweepWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<IntrayIngestSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>How recently an unmarked item must have arrived to be processed rather than merely marked.</summary>
    private static readonly TimeSpan ArrivalWindow = TimeSpan.FromDays(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Worker} started (poll interval {Interval}).", nameof(IntrayIngestSweepWorker), PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // A sweep that throws must not take the worker down with it: the next poll is a fresh attempt,
                // and the marker makes repeating one harmless.
                logger.LogWarning(e, "Intray ingest sweep failed; retrying at the next poll.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IntrayIngestPipeline>();

        // REFUSED WITHOUT WORKING CONDITIONAL WRITES (#1427). The sweep's exclusion is a claim object taken
        // with If-None-Match; a store that ignores that header answers 200 to every instance and each one
        // ingests the same file, filing it as that many documents. Probed once, and the probe logs Fatal with
        // the remedy — stopping loudly is recoverable, silently duplicating everything is not.
        var probe = scope.ServiceProvider.GetRequiredService<ConditionalWriteProbe>();

        // Every active user's own intray. The listing IS the emptiness check, so there is no cheaper pre-filter
        // to apply first — which is also why this polls in minutes rather than seconds.
        var users = await dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, u.TenantId })
            .ToListAsync(cancellationToken);

        // The probe needs a tenant to root its key against (the bucket is derived from the key), so it runs
        // with the first tenant this sweep is about to touch. The answer is cached after the first call, so
        // this costs two writes once per process rather than once per sweep.
        if (users.Count > 0
            && !await probe.SupportedAsync(
                Application.Abstractions.ObjectKeyPrefixes.CapabilityProbe(users[0].TenantId), cancellationToken))
        {
            return;
        }

        foreach (var user in users)
        {
            var prefix = IntrayScopePrefix.ForUser(user.TenantId, user.Id);
            var objects = await storage.ListObjectsAsync(prefix, cancellationToken);
            var names = objects.Select(o => o.Key[prefix.Length..]).ToHashSet(StringComparer.Ordinal);

            foreach (var storageObject in objects)
            {
                var name = storageObject.Key[prefix.Length..];
                if (IsSidecar(name) || names.Contains(name + IntrayIngestPipeline.MarkerSuffix))
                {
                    continue;
                }

                // Old enough to predate the feature: record that it has been seen, and leave the file alone.
                if (storageObject.LastModified is { } modified && DateTimeOffset.UtcNow - modified > ArrivalWindow)
                {
                    await pipeline.MarkSeenAsync(prefix, name, cancellationToken);
                    continue;
                }

                // CLAIM BEFORE INGESTING (#1427). Both instances list before either writes anything, so the
                // marker check above cannot exclude anything on its own — it is a read-then-act, and the
                // result was one dropped file becoming two documents. The lease is a separate object taken
                // with a conditional create, so the storage layer picks the winner.
                var claimKey = $"{prefix}{name}{IntrayIngestLease.ClaimSuffix}";
                await using var lease = await IntrayIngestLease.TryAcquireAsync(
                    storage, logger, claimKey,
                    objects.FirstOrDefault(o => o.Key == claimKey),
                    TimeProvider.System, cancellationToken);

                if (lease is null)
                {
                    continue; // another instance holds a live lease on this item
                }

                var processed = await pipeline.RunAsync(user.TenantId, user.Id, prefix, name, cancellationToken);

                // DISPOSSESSED WHILE WORKING: another instance judged this lease stale and took the item, so
                // it is already being ingested elsewhere and anything written here is the second copy. The
                // pipeline has run, but the lease is what decides whether this instance may claim the result.
                if (!lease.Held)
                {
                    logger.LogWarning(
                        "Finished ingesting {Item} without holding its lease — another instance took it, so "
                        + "this result is discarded to avoid a duplicate.", name);
                    continue;
                }

                // Worth a line only when something actually changed: nothing ran (empty), or the item came back
                // under its own name, is the overwhelmingly common case and would drown the log.
                if (processed.Count > 0 && (processed.Count > 1 || processed[0] != name))
                {
                    logger.LogInformation(
                        "Intray sweep processed {Item} into {Result}.", name, string.Join(", ", processed));
                }
            }
        }
    }

    private static bool IsSidecar(string name) =>
        name.EndsWith(IntrayIngestLease.ClaimSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(IntrayIngestPipeline.MarkerSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(IntrayIngestPipeline.SignedSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mask.json", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".preview.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase);
}
