using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// Expires the bytes an intray or check-out overwrite set aside (#799).
/// </summary>
/// <remarks>
/// <para>
/// <b>The net had no drain.</b> Every write that can replace an intray item — the plain <c>PUT</c>, the
/// safe-save swap, the flat rename — first copies the outgoing bytes to <c>inbox-previous/</c>, and the
/// check-out stash does the same into <c>stash-previous/</c> (#794). Nothing has ever read or removed them, so
/// they accumulated in every tenant's bucket for as long as the installation has run.
/// </para>
/// <para>
/// <b>Thirty days</b> (owner-decided): long enough that somebody noticing a mangled file the following month
/// can still be helped, short enough to bound the growth. The volume is one object per item NAME rather than
/// per save, so it is bounded by how many distinct files pass through an intray — thirty days of that is
/// small, and the retrieval path (#799's other half) is what makes the window meaningful rather than
/// arbitrary.
/// </para>
/// <para>
/// <b>Deletion is the whole operation, so there is no claim.</b> Two instances both sweeping delete the same
/// objects, and a delete that arrives twice is the same as one — unlike the ingest sweep beside it, where the
/// side effect is creating a document and running it twice produces two (#1427). A sweep whose action is
/// idempotent does not need to be made exclusive, and adding a lease here would be ceremony that implies a
/// danger that is not present.
/// </para>
/// </remarks>
public sealed class PreservedBytesSweepWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PreservedBytesSweepWorker> logger) : BackgroundService
{
    /// <summary>Hourly: the work is a listing per user and the window is measured in weeks.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>How long a set-aside copy is kept before it expires.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A storage hiccup must not end the worker: the next tick simply tries again, and the objects
                // this one did not reach are a day older rather than lost.
                logger.LogError(exception, "The preserved-bytes sweep failed; it will run again.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

        var users = await dbContext.Users
            .IgnoreQueryFilters(["TenantFilter"])
            .Select(u => new { u.Id, u.TenantId })
            .ToListAsync(cancellationToken);

        var expired = await PreservedBytesSweep.RunAsync(
            storage,
            users.Select(u => (u.TenantId, u.Id)).ToList(),
            DateTimeOffset.UtcNow,
            logger,
            cancellationToken);

        // Only when something happened: an hourly line saying nothing expired would be 24 lines a day
        // reporting that a net nobody needed caught nothing.
        if (expired > 0)
        {
            logger.LogInformation("Expired {Count} preserved copies older than {Lifetime}.", expired, Lifetime);
        }
    }
}

/// <summary>
/// What the sweep actually does, without a host — so it can be tested against a storage fake.
/// </summary>
/// <remarks>
/// Separated because the worker's own body is a timer, a scope and a user query, and none of those is the
/// behaviour worth pinning. What is worth pinning is which keys it deletes: this sweep DELETES, and its blast
/// radius is live intray items that have no versions and no soft-delete to fall back on, so a prefix mistake
/// here would be unrecoverable in exactly the way the preserved copies exist to prevent.
/// </remarks>
public static class PreservedBytesSweep
{
    /// <summary>Removes every preserved copy older than the lifetime; returns how many went.</summary>
    public static async Task<int> RunAsync(
        IObjectStorageClient storage,
        IReadOnlyList<(Guid TenantId, Guid UserId)> users,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var cutoff = now - PreservedBytesSweepWorker.Lifetime;
        var expired = 0;

        foreach (var (tenantId, userId) in users)
        {
            // BOTH areas, because they are the same net under two names: the intray's and the check-out
            // stash's. Sweeping one and not the other is how half a policy ships and nobody notices the half
            // that did not — the stash area was added by the same change and is the less visible of the two.
            foreach (var prefix in new[]
                     {
                         ObjectKeyPrefixes.UserInboxPrevious(tenantId, userId),
                         ObjectKeyPrefixes.UserStashPrevious(tenantId, userId),
                     })
            {
                foreach (var stale in (await storage.ListObjectsAsync(prefix, cancellationToken))
                         .Where(o => o.LastModified < cutoff))
                {
                    await storage.DeleteObjectAsync(stale.Key, cancellationToken);
                    expired++;
                }
            }
        }

        return expired;
    }
}
