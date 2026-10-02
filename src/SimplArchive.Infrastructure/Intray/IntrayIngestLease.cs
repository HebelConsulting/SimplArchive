using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// A heartbeat lease over one intray item, held in object storage, so two app instances cannot ingest the
/// same dropped file (#1427).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lease and not the marker.</b> The sweep used to skip anything whose <c>.ingest.json</c> marker
/// existed — but that marker is a RESULT, written after the work, so two sweeps both looked before either
/// wrote and one file became two documents. Claiming with the result marker instead would record done-before-
/// doing, which is exactly why <c>EmailNotificationDispatcher</c> needed a third state (ADR 0840). So the claim
/// is its own object and the result marker keeps its meaning.
/// </para>
/// <para>
/// <b>Why a heartbeat rather than a fixed expiry.</b> A claim with no expiry turns "ingested twice" into
/// "never ingested": a process killed mid-ingest — which every rolling update does by design (ADR 0808) —
/// leaves a claim nobody will ever clear. But a fixed expiry has to exceed the WORST-CASE ingest (OCR on a
/// large multi-page scan) or it steals work from a healthy instance and produces the very duplicate this
/// exists to prevent. Renewing while the work runs decouples the two: the window only has to outlast a
/// missed BEAT, not the job.
/// </para>
/// <para>
/// <b>And an absolute cap, because a beat proves the PROCESS is alive, not that progress is happening.</b> A
/// hung ingest — a blocked sidecar call in a healthy container — would renew its lease forever and strand the
/// file just as thoroughly as the crash this is meant to survive. So a lease is reclaimable after missed beats
/// OR after <see cref="AbsoluteCap"/> regardless of them, measured from the ORIGINAL acquisition recorded in
/// the claim body rather than from <c>LastModified</c>, which every beat moves.
/// </para>
/// <para>
/// <b>Two clocks, acknowledged.</b> Staleness compares this process's clock against the STORE's
/// <c>LastModified</c>. The windows below are minutes, against hosts that are normally NTP-synced to within
/// seconds, so the margin absorbs ordinary skew — but a badly-skewed clock makes leases look permanently
/// stale (constant takeovers, therefore duplicates) or permanently fresh (strandings), and neither symptom
/// names the clock. If this is ever suspected, compare the store's own timestamps against the app's before
/// looking anywhere else.
/// </para>
/// </remarks>
public sealed class IntrayIngestLease : IAsyncDisposable
{
    /// <summary>The claim object's suffix. Treated as a sidecar everywhere a listing is read.</summary>
    public const string ClaimSuffix = ".ingest.claim";

    /// <summary>How often the holder renews, while it works.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    /// <summary>How long since the last renewal before another instance may take over — four missed beats.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>The ceiling a lease cannot renew past, so a HUNG holder is eventually reclaimed.</summary>
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromMinutes(30);

    private readonly IObjectStorageClient _storage;
    private readonly ILogger _logger;
    private readonly string _claimKey;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _beating;

    /// <summary>Set when a renewal is refused: somebody judged this lease stale and took it.</summary>
    private volatile bool _lost;

    private string _eTag;
    private readonly DateTimeOffset _acquiredAt;

    private IntrayIngestLease(
        IObjectStorageClient storage, ILogger logger, string claimKey, string eTag, DateTimeOffset acquiredAt)
    {
        _storage = storage;
        _logger = logger;
        _claimKey = claimKey;
        _eTag = eTag;
        _acquiredAt = acquiredAt;
        _beating = BeatAsync(_stop.Token);
    }

    /// <summary>
    /// Whether this holder still owns the lease. A holder that has lost it must ABANDON its work.
    /// </summary>
    /// <remarks>
    /// Finishing anyway is the duplicate: the instance that took over is already ingesting the same file, so a
    /// dispossessed holder that carries on produces the second document. Checked before anything is written.
    /// </remarks>
    public bool Held => !_lost;

    /// <summary>
    /// Takes the lease on one item, or returns null when another instance holds a LIVE one.
    /// </summary>
    /// <param name="existing">
    /// The claim object as the caller's listing already saw it, or null when there is none — so the common
    /// case costs no extra round trip.
    /// </param>
    public static async Task<IntrayIngestLease?> TryAcquireAsync(
        IObjectStorageClient storage,
        ILogger logger,
        string claimKey,
        StorageObject? existing,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var body = Body(now);

        if (existing is null)
        {
            // Nobody holds it. The conditional create is what makes this exclusive: two instances reaching
            // here together produce one winner and one 412.
            using var content = new MemoryStream(body);
            if (!await storage.TryPutIfAbsentAsync(claimKey, content, "application/json", cancellationToken))
            {
                return null;
            }

            return await AttachAsync(storage, logger, claimKey, now, cancellationToken);
        }

        if (!await IsReclaimableAsync(storage, existing, now, logger, cancellationToken))
        {
            return null;
        }

        // A takeover, conditioned on the exact claim this caller judged stale. If it has moved since — the
        // holder renewed, or another instance took it first — this fails and we leave it alone.
        if (existing.ETag is not { Length: > 0 } eTag)
        {
            logger.LogWarning(
                "The claim {ClaimKey} looks stale but the listing carried no ETag, so it cannot be taken over "
                + "safely; leaving it. A takeover without one would be a blind overwrite, which can wipe a "
                + "claim another instance has just created.",
                claimKey);

            return null;
        }

        using var takeover = new MemoryStream(body);
        if (!await storage.TryPutIfMatchAsync(claimKey, eTag, takeover, "application/json", cancellationToken))
        {
            return null;
        }

        logger.LogInformation(
            "Took over a stale intray ingest claim {ClaimKey}; its holder stopped renewing.", claimKey);

        return await AttachAsync(storage, logger, claimKey, now, cancellationToken);
    }

    /// <summary>Whether a held claim may be taken: its holder stopped beating, or it has run past the cap.</summary>
    /// <remarks>
    /// The two conditions cover two different failures, and only the first is obvious. A holder that STOPPED —
    /// killed by a rolling update — stops renewing, so <c>LastModified</c> ages out. A holder that HUNG keeps
    /// renewing from a healthy process while making no progress, so <c>LastModified</c> never ages and only the
    /// cap can free the item. The cap is therefore measured from the ACQUISITION recorded in the body, which
    /// renewals deliberately preserve, rather than from <c>LastModified</c>, which every renewal moves.
    /// </remarks>
    private static async Task<bool> IsReclaimableAsync(
        IObjectStorageClient storage, StorageObject claim, DateTimeOffset now, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (now - claim.LastModified > StaleAfter)
        {
            return true;
        }

        try
        {
            await using var content = await storage.GetObjectAsync(claim.Key, cancellationToken);
            var body = await JsonSerializer.DeserializeAsync<ClaimBody>(content, cancellationToken: cancellationToken);
            if (body is null)
            {
                return false;
            }

            if (now - body.AcquiredAt <= AbsoluteCap)
            {
                return false;
            }

            logger.LogWarning(
                "The intray ingest claim {ClaimKey} has been renewed for over {Cap} without finishing — its "
                + "holder {Holder} is alive but making no progress. Taking it over. A beat proves the process "
                + "is running, never that the work is moving, which is what this cap is for.",
                claim.Key, AbsoluteCap, body.Holder);

            return true;
        }
        catch (Exception exception)
        {
            // An unreadable claim is left alone rather than treated as reclaimable: guessing "stale" here
            // would hand the item to a second instance while the first may well still be working on it.
            logger.LogWarning(exception,
                "Could not read the intray ingest claim {ClaimKey} to check its age; leaving it held.",
                claim.Key);

            return false;
        }
    }

    private static byte[] Body(DateTimeOffset acquiredAt) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ClaimBody(acquiredAt, Environment.MachineName)));

    private static async Task<IntrayIngestLease> AttachAsync(
        IObjectStorageClient storage, ILogger logger, string claimKey, DateTimeOffset acquiredAt,
        CancellationToken cancellationToken)
    {
        // The ETag the renewals will condition on. Read back rather than guessed: a store may normalise or
        // compute it differently, and a renewal built on a guessed tag would fail every time — which looks
        // exactly like being dispossessed, and would make every holder abandon its own work.
        var listing = await storage.ListObjectsAsync(claimKey, cancellationToken);
        var eTag = listing.FirstOrDefault(o => o.Key == claimKey)?.ETag ?? string.Empty;

        return new IntrayIngestLease(storage, logger, claimKey, eTag, acquiredAt);
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Heartbeat, cancellationToken);

                if (string.IsNullOrEmpty(_eTag))
                {
                    return; // nothing to condition on; the lease simply expires on its own
                }

                using var content = new MemoryStream(Body(_acquiredAt));
                if (!await _storage.TryPutIfMatchAsync(_claimKey, _eTag, content, "application/json", cancellationToken))
                {
                    // DISPOSSESSED. Somebody judged this lease stale and took it, so this instance's work is
                    // now a duplicate in progress. Say so loudly: it means a beat was missed, which on a
                    // healthy host means the work blocked long enough to look dead.
                    _lost = true;
                    _logger.LogWarning(
                        "Lost the intray ingest lease {ClaimKey} to another instance — this instance's work on "
                        + "it is abandoned. A lease is taken only after {Stale} without a renewal, so either "
                        + "this process stalled or its clock disagrees with the object store's.",
                        _claimKey, StaleAfter);

                    return;
                }

                var listing = await _storage.ListObjectsAsync(_claimKey, cancellationToken);
                _eTag = listing.FirstOrDefault(o => o.Key == _claimKey)?.ETag ?? string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // The ordinary end: the work finished and disposed the lease.
        }
        catch (Exception exception)
        {
            // A beat that throws must not take the host down, and must not be silent either — the lease will
            // now expire under a holder that is still working, which is how a duplicate happens.
            _logger.LogError(exception,
                "The intray ingest lease {ClaimKey} could not be renewed. It will expire while this instance "
                + "is still working, so another may take the item and ingest it a second time.", _claimKey);
        }
    }

    /// <summary>Stops beating and RELEASES the claim, so a retry need not wait out the window.</summary>
    /// <remarks>
    /// Released on failure as well as success: a transient fault should cost the next sweep, not two minutes.
    /// A CRASH releases nothing, which is the case the lease exists for.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        try
        {
            await _beating;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        if (!_lost)
        {
            try
            {
                await _storage.DeleteObjectAsync(_claimKey, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Not fatal: an unreleased claim expires on its own. Worth a line, because it means the next
                // attempt at this item waits a window it should not have had to.
                _logger.LogWarning(exception,
                    "Could not release the intray ingest claim {ClaimKey}; it will expire instead.", _claimKey);
            }
        }

        _stop.Dispose();
    }

    private sealed record ClaimBody(DateTimeOffset AcquiredAt, string Holder);
}
