using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// Asks the configured object store, once, whether it really honours conditional writes (#1427).
/// </summary>
/// <remarks>
/// <para>
/// <b>Because the dangerous answer is not an error.</b> A store that does not support
/// <c>If-None-Match: *</c> may REFUSE the write, which is loud and fixable — or it may IGNORE the header and
/// answer 200 to both writers, which silently restores the duplicate the lease exists to prevent, on an
/// installation that has no way to tell. This project points at whatever S3-compatible store an operator
/// configures, so the capability is measured rather than assumed.
/// </para>
/// <para>
/// <b>The sweep is refused without it</b> (owner-decided, 2026-10-02). An ingest that stops and says so is
/// recoverable; one that files every document twice is not, and the operator cannot see the difference from
/// the outside — the user reports it as having dropped the file twice.
/// </para>
/// <para>
/// Measured on the pinned SeaweedFS while this was written: the first conditional write answers 200, the
/// second 412, and the first writer's bytes survive.
/// </para>
/// </remarks>
public sealed class ConditionalWriteProbe(IObjectStorageClient storage, ILogger<ConditionalWriteProbe> logger)
{
    private bool? _supported;

    /// <summary>Whether conditional writes can be relied on, probing once and remembering the answer.</summary>
    public async Task<bool> SupportedAsync(string probePrefix, CancellationToken cancellationToken = default)
    {
        if (_supported is { } known)
        {
            return known;
        }

        // A key nobody else will touch, so a concurrent probe on another instance cannot be mistaken for the
        // store ignoring the header — which would make two healthy instances each conclude the store is broken.
        var key = $"{probePrefix}conditional-write-probe/{Guid.NewGuid():N}";

        try
        {
            using var first = new MemoryStream("probe"u8.ToArray());
            if (!await storage.TryPutIfAbsentAsync(key, first, "text/plain", cancellationToken))
            {
                // A fresh GUID that already exists is not a thing; something is wrong enough not to guess at.
                logger.LogCritical(
                    "The object store refused a conditional write to a brand-new key, which should be "
                    + "impossible. Intray ingest is DISABLED: without working conditional writes two app "
                    + "instances would ingest the same dropped file twice.");

                return (_supported = false).Value;
            }

            using var second = new MemoryStream("probe-again"u8.ToArray());
            var secondWon = await storage.TryPutIfAbsentAsync(key, second, "text/plain", cancellationToken);

            _supported = !secondWon;
            if (secondWon)
            {
                // THE SILENT CASE. The store accepted a write that was conditioned on the key being absent,
                // over a key that was present — so the condition is being ignored and no claim excludes
                // anything.
                logger.LogCritical(
                    "The configured object store IGNORES conditional writes: a second write to an existing key "
                    + "with If-None-Match succeeded. Intray ingest is DISABLED, because every file dropped "
                    + "into an intray would be ingested once per app instance and filed as that many separate "
                    + "documents. Point ObjectStorage at a store that honours If-None-Match (AWS S3 and "
                    + "SeaweedFS both do), or run a single app instance.");
            }

            return _supported.Value;
        }
        catch (Exception exception)
        {
            // Refusing is the safe direction: a store whose answer we could not get is one we cannot claim on.
            logger.LogCritical(exception,
                "Could not determine whether the object store honours conditional writes. Intray ingest is "
                + "DISABLED rather than run without the exclusion it depends on.");

            return (_supported = false).Value;
        }
        finally
        {
            try
            {
                await storage.DeleteObjectAsync(key, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not delete the conditional-write probe object {Key}.", key);
            }
        }
    }
}
