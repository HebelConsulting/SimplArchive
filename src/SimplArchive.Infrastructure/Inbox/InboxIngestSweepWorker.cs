using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Inbox;

/// <summary>
/// The backstop for the inbox ingest pipeline (issue #494): items that arrived with no client to signal it.
/// </summary>
/// <remarks>
/// <para>
/// It exists because <b>the inbox is also a WebDAV mount</b> (ADR 0509). A file dropped into the mounted folder
/// never touches the upload endpoint, so the client-signalled path cannot see it — and neither can a browser
/// tab that was closed between the storage PUT and the call that follows it. Without this sweep the automatic
/// straightening would quietly not apply to a whole ingest path, which is the kind of gap a user reports as
/// "it works for some of my scans".
/// </para>
/// <para>
/// <b>It is a backstop, not a migration.</b> Every item already in an inbox when this ships has no marker, so a
/// naive sweep would treat the entire existing inbox as new and convert every scan in it — a bulk rewrite of
/// files people already have, triggered by nobody pressing anything. So an unmarked item older than
/// <see cref="ArrivalWindow"/> is marked as seen WITHOUT being processed. The window is generous enough to
/// cover a file dropped over a weekend and finished on Monday, and short enough that history is left alone.
/// </para>
/// <para>
/// Own inboxes only. A group inbox has no user whose preference to read (the setting is per-user, #491), and
/// guessing whose it is would be worse than leaving those to the client-signalled path, where the person who
/// uploaded is known.
/// </para>
/// </remarks>
public sealed class InboxIngestSweepWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<InboxIngestSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>How recently an unmarked item must have arrived to be processed rather than merely marked.</summary>
    private static readonly TimeSpan ArrivalWindow = TimeSpan.FromDays(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Worker} started (poll interval {Interval}).", nameof(InboxIngestSweepWorker), PollInterval);

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
                logger.LogWarning(e, "Inbox ingest sweep failed; retrying at the next poll.");
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
        var pipeline = scope.ServiceProvider.GetRequiredService<InboxIngestPipeline>();

        // Every active user's own inbox. The listing IS the emptiness check, so there is no cheaper pre-filter
        // to apply first — which is also why this polls in minutes rather than seconds.
        var users = await dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, u.TenantId })
            .ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            var prefix = InboxScopePrefix.ForUser(user.TenantId, user.Id);
            var objects = await storage.ListObjectsAsync(prefix, cancellationToken);
            var names = objects.Select(o => o.Key[prefix.Length..]).ToHashSet(StringComparer.Ordinal);

            foreach (var storageObject in objects)
            {
                var name = storageObject.Key[prefix.Length..];
                if (IsSidecar(name) || names.Contains(name + InboxIngestPipeline.MarkerSuffix))
                {
                    continue;
                }

                // Old enough to predate the feature: record that it has been seen, and leave the file alone.
                if (storageObject.LastModified is { } modified && DateTimeOffset.UtcNow - modified > ArrivalWindow)
                {
                    await pipeline.MarkSeenAsync(prefix, name, cancellationToken);
                    continue;
                }

                var processed = await pipeline.RunAsync(user.TenantId, user.Id, prefix, name, cancellationToken);
                if (processed is not null && processed != name)
                {
                    logger.LogInformation("Inbox sweep processed {Item} into {Result}.", name, processed);
                }
            }
        }
    }

    private static bool IsSidecar(string name) =>
        name.EndsWith(InboxIngestPipeline.MarkerSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(InboxIngestPipeline.SignedSuffix, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mask.json", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".preview.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".textlayout.json", StringComparison.OrdinalIgnoreCase);
}
