using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Mail;

/// <summary>
/// Empties the ephemeral mail prefix (#640): discarded messages past their window, and the objects no version
/// references any more.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <c>Junk</c> and <c>Trash</c> are ever swept</b>, and only after a configurable window (default 30
/// days, <c>EphemeralMail:RetentionDays</c>). Everything else — <c>Inbox</c>, <c>Drafts</c>, <c>Sent</c> —
/// stays forever. This is a disposal policy for what the user has already discarded, not a deadline on their
/// mail, which is why it needs no disposition review: an inbox where deleting is just deleting is the whole
/// reason the ephemeral tier exists (ADR 0628).
/// </para>
/// <para>
/// <b>Two halves, and the second is the load-bearing one.</b> Sweeping folders reclaims what the user threw
/// away; but <c>DocumentMover</c> deliberately leaves the ephemeral copy behind whenever a message is filed out
/// (ADR 0638 — deleting before the caller's save turns a failed save into a row pointing at absent bytes). With
/// only the folder half, every message ever filed out of <c>Inbox</c> would leave a copy nothing collects, and
/// the prefix would grow without bound while the sweep appeared to be working.
/// </para>
/// <para>
/// The orphan half keys off the <b>object store</b>, not the database: it lists the mail prefix and deletes
/// what no <c>DocumentVersion</c> claims. That is the only direction that can find a stranded object, since by
/// definition nothing in the database points at it.
/// </para>
/// </remarks>
public sealed class EphemeralMailSweepWorker : BackgroundService
{
    // Long enough that a restarting app is not sweeping while it is still opening connections, and short enough
    // that a test or a demo does not have to wait a working day to see it happen.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EphemeralMailSweepWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _retentionDays;

    public EphemeralMailSweepWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<EphemeralMailSweepWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _retentionDays = configuration.GetValue<int?>("EphemeralMail:RetentionDays") is { } d && d > 0 ? d : 30;
        var hours = configuration.GetValue<int?>("EphemeralMail:SweepIntervalHours") is { } h && h > 0 ? h : 6;
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Worker} started (every {Interval}, discarding Junk/Trash after {RetentionDays} days).",
            nameof(EphemeralMailSweepWorker), _interval, _retentionDays);

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

    /// <summary>One pass: discarded messages, then stranded objects. Public so a test can drive it directly.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

            var discarded = await SweepDiscardedAsync(db, storage, cancellationToken);
            var stranded = await SweepStrandedObjectsAsync(db, storage, cancellationToken);

            if (discarded > 0 || stranded > 0)
            {
                _logger.LogInformation(
                    "Ephemeral mail sweep: discarded {Discarded} message(s) past {RetentionDays} days, and "
                    + "reclaimed {Stranded} stranded object(s) left behind by filing.",
                    discarded, _retentionDays, stranded);
            }
        }
        catch (Exception e)
        {
            // A sweep that throws must not take the host down, and must not go quiet: the next pass retries,
            // and the administrator needs to know reclamation has stopped.
            _logger.LogError(e, "Ephemeral mail sweep failed; the next pass will retry.");
        }
    }

    // ---- Half one: what the user discarded ----------------------------------------------------------

    private async Task<int> SweepDiscardedAsync(SimplArchiveDbContext db, IObjectStorageClient storage, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);

        // The tenant filter is off deliberately: this is a host-wide background pass with no current tenant,
        // exactly as the retention and audit sweeps run. Soft-delete is ignored too — a message soft-deleted in
        // Trash is doubly discarded, not exempt.
        var candidates = await db.Documents.IgnoreQueryFilters()
            .Where(d => d.StagedAt != null && d.StagedAt < cutoff)
            .Select(d => new
            {
                d.Id,
                d.TenantId,
                d.Name,
                d.CheckedOutByUserId,
                FolderName = db.Documents.Where(p => p.Id == d.ParentId).Select(p => p.Name).FirstOrDefault(),
                ParentMaskId = db.Documents.Where(p => p.Id == d.ParentId)
                    .Select(p => db.MaskVersions.Where(mv => mv.Id == p.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault())
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Only the two discard folders. Asked by NAME after the mask has said "this is a staging folder",
        // which is the same two-step the IMAP projection uses: the mask says what kind of folder it is, the
        // name says which of the five.
        var sweepable = candidates
            .Where(c => c.ParentMaskId == WellKnownMaskIds.ImapSpecial && SweptFolderNames.Contains(c.FolderName ?? string.Empty))
            .ToList();

        var swept = 0;
        foreach (var candidate in sweepable)
        {
            if (candidate.CheckedOutByUserId is not null)
            {
                // Someone is editing a working copy of it. Deleting the original under them is the same class
                // of surprise a hold protects against, so it waits — and says so, since nothing else would.
                _logger.LogWarning(
                    "Ephemeral mail sweep: {DocumentName} is past its window in {Folder} but is CHECKED OUT, so "
                    + "it is being kept. It will be swept once checked in.",
                    candidate.Name, candidate.FolderName);
                continue;
            }

            if (await IsUnderLegalHoldAsync(db, candidate.Id, cancellationToken))
            {
                // A hold is not a retention policy. Being outside the archive's retention rules does not put a
                // message outside a legal hold, and a sweep that quietly ignored one would be the worst kind of
                // compliance failure — invisible.
                _logger.LogWarning(
                    "Ephemeral mail sweep: {DocumentName} is past its window in {Folder} but is under a LEGAL "
                    + "HOLD, so it is being kept. Release the hold to let it be discarded.",
                    candidate.Name, candidate.FolderName);
                continue;
            }

            var keys = await db.DocumentVersions.IgnoreQueryFilters()
                .Where(v => v.DocumentId == candidate.Id)
                .Select(v => v.ObjectKey)
                .ToListAsync(cancellationToken);

            // Rows first here, unlike delivery. A row with no bytes is a message that opens to an error; an
            // object with no row is exactly what the other half of this sweep collects — so if the process dies
            // between the two, the recoverable state is the one that is already handled.
            var document = await db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == candidate.Id, cancellationToken);
            if (document is null)
            {
                continue;
            }

            db.Documents.Remove(document);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var key in keys)
            {
                _logger.LogTrace("Ephemeral mail sweep: deleting {ObjectKey} for discarded message {DocumentId}", key, candidate.Id);
                await storage.DeleteObjectAsync(key, cancellationToken);
            }

            swept++;
        }

        return swept;
    }

    /// <summary>The two folders that are ever swept — everything else keeps its mail (#640).</summary>
    private static readonly HashSet<string> SweptFolderNames =
        new(StringComparer.Ordinal) { "Junk", "Trash" };

    private static async Task<bool> IsUnderLegalHoldAsync(SimplArchiveDbContext db, Guid documentId, CancellationToken cancellationToken) =>
        await db.LegalHoldItems.IgnoreQueryFilters()
            .AnyAsync(i => i.DocumentId == documentId
                && db.LegalHolds.IgnoreQueryFilters().Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null), cancellationToken);

    // ---- Half two: what filing left behind ----------------------------------------------------------

    private async Task<int> SweepStrandedObjectsAsync(SimplArchiveDbContext db, IObjectStorageClient storage, CancellationToken cancellationToken)
    {
        // Every tenant that has ever staged mail. Listing per tenant rather than globally because the object
        // store is partitioned per tenant bucket (ADR 0372).
        var tenantIds = await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).ToListAsync(cancellationToken);

        var reclaimed = 0;
        foreach (var tenantId in tenantIds)
        {
            List<StorageObject> objects;
            try
            {
                objects = [.. await storage.ListObjectsAsync($"tenants/{tenantId}/users/", cancellationToken)];
            }
            catch (Exception e)
            {
                // A tenant whose bucket does not exist yet has staged nothing. Anything else is worth naming.
                _logger.LogDebug(e, "Ephemeral mail sweep: could not list the mail prefix for tenant {TenantId}", tenantId);
                continue;
            }

            var mailObjects = objects.Where(o => ObjectKeyBuilder.IsEphemeralMailKey(o.Key)).ToList();
            if (mailObjects.Count == 0)
            {
                continue;
            }

            var keys = mailObjects.Select(o => o.Key).ToList();
            var claimed = await db.DocumentVersions.IgnoreQueryFilters()
                .Where(v => keys.Contains(v.ObjectKey))
                .Select(v => v.ObjectKey)
                .ToListAsync(cancellationToken);

            var claimedSet = new HashSet<string>(claimed, StringComparer.Ordinal);
            foreach (var stranded in mailObjects.Where(o => !claimedSet.Contains(o.Key)))
            {
                _logger.LogTrace(
                    "Ephemeral mail sweep: reclaiming {ObjectKey} — no version references it (tenant {TenantId})",
                    stranded.Key, tenantId);
                await storage.DeleteObjectAsync(stranded.Key, cancellationToken);
                reclaimed++;
            }
        }

        return reclaimed;
    }
}
