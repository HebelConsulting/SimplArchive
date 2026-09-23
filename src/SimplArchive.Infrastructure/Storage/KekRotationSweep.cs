using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Re-wraps every encrypted object into the current KEK generation (ADR 0014), so an old generation can
/// eventually be retired. Runs as a singleton background job kicked off by the admin rotate action —
/// deliberately not on a schedule (owner decision: unattended I/O against every tenant's bucket is a
/// surprise nobody asked for).
/// </summary>
/// <remarks>
/// <para>
/// <b>No blob is ever read or rewritten.</b> A DEK is unchanged by re-wrapping; only its RSA wrapping
/// moves generation. So each object costs one HEAD (to read its metadata), one small HSM call, and one
/// metadata write — which is what makes rotating a multi-terabyte archive affordable at all.
/// </para>
/// <para>
/// <b>Restartable and idempotent by construction.</b> The work list is derived from the objects
/// themselves — an object whose <c>sa-kek-generation</c> is already current is simply skipped — so a sweep
/// interrupted by a restart resumes correctly with no checkpoint to keep, and running it twice is a no-op
/// the second time. That is also why the progress figure is recomputed rather than remembered.
/// </para>
/// <para>
/// <b>One failure never stops the sweep.</b> An object that refuses (a generation already retired, a
/// corrupt wrap) is counted and named at Warning; the remaining objects still move, because the whole
/// point of the sweep is to drive the old generation's reference count toward zero.
/// </para>
/// </remarks>
public sealed class KekRotationSweep(
    IServiceScopeFactory scopeFactory,
    AtRestKeyService keys,
    ILogger<KekRotationSweep> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What a status surface reports — recomputed from the objects, never remembered.</summary>
    public sealed record SweepStatus(bool Running, string? Current, IReadOnlyList<string> Generations,
        int Rewrapped, int Remaining, int Failed);

    private volatile bool _running;
    private int _rewrapped;
    private int _remaining;
    private int _failed;

    public SweepStatus Status(string? current = null, IReadOnlyList<string>? generations = null) =>
        new(_running, current, generations ?? [], _rewrapped, _remaining, _failed);

    /// <summary>
    /// Sweeps every tenant's bucket. Only one sweep runs at a time — a second request while one is in
    /// flight is a no-op rather than a second pass over the same objects.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            logger.LogInformation("A KEK re-wrap sweep is already running; ignoring the request.");
            return;
        }

        _running = true;
        _rewrapped = _remaining = _failed = 0;
        try
        {
            var (current, _) = await keys.GenerationsAsync(cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
            var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

            // No ambient tenant on a background path — the standing rule for pre-tenant lookups.
            var tenantIds = await dbContext.Tenants.IgnoreQueryFilters(["TenantFilter"])
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            foreach (var tenantId in tenantIds)
            {
                var prefix = ObjectKeyPrefixes.Tenant(tenantId);
                if (!await keys.GatedAsync(prefix + "probe", cancellationToken))
                {
                    continue; // not an encrypted tenant — nothing of ours lives in that bucket
                }

                foreach (var stored in await storage.ListObjectsAsync(prefix, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RewrapOneAsync(storage, stored.Key, current, cancellationToken);
                }
            }

            logger.LogInformation(
                "KEK re-wrap sweep finished: {Rewrapped} re-wrapped into {Current}, {Remaining} still on an "
                + "older generation, {Failed} failed.", _rewrapped, current, _remaining, _failed);
        }
        finally
        {
            _running = false;
            _gate.Release();
        }
    }

    private async Task RewrapOneAsync(
        IObjectStorageClient storage, string objectKey, string current, CancellationToken cancellationToken)
    {
        try
        {
            var info = await storage.GetObjectInfoAsync(objectKey, cancellationToken);
            if (!info.Metadata.TryGetValue(EncryptingObjectStorageClient.WrappedDekKey, out var wrappedDek)
                || !info.Metadata.TryGetValue(EncryptingObjectStorageClient.KekGenerationKey, out var generation))
            {
                return; // plaintext object (the mixed state ADR 0818 allows) — nothing to re-wrap
            }

            if (generation == current)
            {
                return; // already current: what makes the sweep idempotent and restartable
            }

            var (rewrapped, newGeneration) = await keys.RewrapDekAsync(wrappedDek, generation, cancellationToken);

            // Metadata only. The ciphertext is byte-identical before and after a rotation.
            var metadata = new Dictionary<string, string>(info.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [EncryptingObjectStorageClient.WrappedDekKey] = rewrapped,
                [EncryptingObjectStorageClient.KekGenerationKey] = newGeneration,
            };
            await storage.SetObjectMetadataAsync(objectKey, metadata, cancellationToken);
            Interlocked.Increment(ref _rewrapped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Named, counted, and the sweep continues: the goal is to drive the old generation's
            // reference count DOWN, and one bad object must not hold the rest hostage.
            Interlocked.Increment(ref _failed);
            Interlocked.Increment(ref _remaining);
            logger.LogWarning(exception, "Object {ObjectKey} could not be re-wrapped into the current KEK "
                + "generation; it stays on its old one.", objectKey);
        }
    }

    /// <summary>
    /// Counts every OBJECT VERSION still wrapped by a generation other than the current one — the honest
    /// answer to "may I retire yet?", computed from the store rather than from a remembered tally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Versions, not current objects, and that distinction is the whole safety of retirement.</b>
    /// Re-wrapping is a self-COPY with metadata REPLACE, which in a versioned bucket writes a NEW version
    /// — so the CURRENT version moves to the new generation while every historical version keeps the
    /// wrapped DEK it was written with. A WORM-locked historical version can never be re-wrapped at all
    /// (that is what Object Lock means), so it holds a reference to its generation until its retention
    /// expires. Counting only current objects would report zero while those references survived, and
    /// retiring then would destroy exactly the records WORM exists to guarantee — the designed failure
    /// mode (ADR 0001) arriving by accident.
    /// </para>
    /// <para>
    /// The practical consequence, stated where somebody will meet it: <b>a KEK's lifetime is bounded below
    /// by the longest WORM retention of anything it wraps</b>. Rotation stays free at any moment; only
    /// retirement waits.
    /// </para>
    /// </remarks>
    public async Task<int> CountRemainingAsync(CancellationToken cancellationToken)
    {
        var (current, _) = await keys.GenerationsAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var remaining = 0;
        var immutable = 0;
        foreach (var tenantId in await dbContext.Tenants.IgnoreQueryFilters(["TenantFilter"])
                     .Select(t => t.Id).ToListAsync(cancellationToken))
        {
            var prefix = ObjectKeyPrefixes.Tenant(tenantId);
            if (!await keys.GatedAsync(prefix + "probe", cancellationToken))
            {
                continue;
            }

            foreach (var version in await storage.ListObjectVersionsAsync(prefix, cancellationToken))
            {
                if (!version.Metadata.TryGetValue(EncryptingObjectStorageClient.KekGenerationKey, out var generation)
                    || generation == current)
                {
                    continue;
                }

                remaining++;
                if (!version.IsLatest)
                {
                    immutable++; // a historical version: the sweep cannot move it, only time can
                }
            }
        }

        if (immutable > 0)
        {
            logger.LogInformation(
                "{Remaining} object version(s) are still wrapped by an older KEK generation, of which "
                + "{Immutable} are HISTORICAL versions the sweep cannot re-wrap — a generation stays "
                + "un-retirable until their WORM retention expires.", remaining, immutable);
        }

        _remaining = remaining;
        return remaining;
    }
}
