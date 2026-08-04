using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Conversion;

// Drains the SearchablePdfOutbox in the background (ADR "Searchable PDF successor for TIFFs"), off the request
// path. Registered only when Ocr:Url is configured. For each row: fetch the source TIFF, OCR it into a
// searchable PDF via the sidecar, store it, and create a Confirmed successor DocumentVersion (attributed to
// the source version's creator, carrying its DocumentDate), then enqueue a search re-index. The successor's
// creation and the row's removal commit together, so a crash can't leave a duplicate version; a conversion
// failure leaves the row (retried) until an attempt cap, after which it's dropped.
public sealed class SearchablePdfWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 10;
    private const int MaxAttempts = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchablePdfWorker> _logger;

    public SearchablePdfWorker(IServiceScopeFactory scopeFactory, ILogger<SearchablePdfWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started (poll interval {Interval}).", nameof(SearchablePdfWorker), PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DrainOnceAsync(stoppingToken))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Searchable-PDF worker loop failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> DrainOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var converter = scope.ServiceProvider.GetRequiredService<ISearchablePdfConverter>();
        var indexQueue = scope.ServiceProvider.GetRequiredService<IDocumentIndexQueue>();
        var storageQuota = scope.ServiceProvider.GetRequiredService<IStorageQuotaService>();

        var batch = await dbContext.SearchablePdfOutbox
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            _logger.LogDebug("Searchable-PDF outbox is empty; nothing to drain.");
            return false;
        }

        var progressed = false;
        foreach (var row in batch)
        {
            tenantAccessor.TenantId = row.TenantId == Guid.Empty ? null : row.TenantId;
            _logger.LogDebug("Processing searchable-PDF outbox row for source version {VersionId} in tenant {TenantId}.", row.SourceVersionId, row.TenantId);
            progressed |= await ProcessAsync(dbContext, storage, converter, indexQueue, storageQuota, row, cancellationToken);
        }

        return progressed;
    }

    private async Task<bool> ProcessAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, ISearchablePdfConverter converter,
        IDocumentIndexQueue indexQueue, IStorageQuotaService storageQuota, SearchablePdfOutbox row, CancellationToken cancellationToken)
    {
        var source = await dbContext.DocumentVersions.SingleOrDefaultAsync(v => v.Id == row.SourceVersionId, cancellationToken);
        if (source is null || source.Status != DocumentVersionStatus.Confirmed)
        {
            // Source gone / never confirmed — nothing to convert.
            dbContext.SearchablePdfOutbox.Remove(row);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        // A TIFF always converts; a PDF converts only if it's a scanned image-only document (ADR "Scanned
        // image-only PDF detection") — a born-digital / already-OCR'd / signed / encrypted PDF is dropped.
        var extension = Path.GetExtension(source.ObjectKey).ToLowerInvariant();
        var kind = extension is ".tif" or ".tiff" ? SearchablePdfSourceKind.Tiff
            : extension == ".pdf" ? SearchablePdfSourceKind.Pdf
            : (SearchablePdfSourceKind?)null;
        if (kind is null)
        {
            dbContext.SearchablePdfOutbox.Remove(row);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        byte[] sourceBytes;
        await using (var stream = await storage.GetObjectAsync(source.ObjectKey, cancellationToken))
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            sourceBytes = buffer.ToArray();
        }

        if (kind == SearchablePdfSourceKind.Pdf && !ScannedPdfDetector.IsConvertibleScan(sourceBytes))
        {
            // Not a scan we should OCR (has a text layer, no bitmap, signed, or unparseable) — no successor.
            dbContext.SearchablePdfOutbox.Remove(row);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        // OCR languages: the version override, else the tenant default (ADR "Per-tenant / per-version OCR
        // languages"). Both hold a Tesseract "+"-joined multi-select of OcrLanguages.Supported codes.
        var languages = source.OcrLanguages;
        if (string.IsNullOrWhiteSpace(languages))
        {
            languages = await dbContext.Tenants
                .Where(t => t.Id == source.TenantId)
                .Select(t => t.DefaultOcrLanguages)
                .SingleOrDefaultAsync(cancellationToken) ?? Domain.Documents.OcrLanguages.Default;
        }

        var pdfBytes = await converter.ConvertToSearchablePdfAsync(sourceBytes, kind.Value, languages, cancellationToken);
        if (pdfBytes is null)
        {
            row.Attempts++;
            if (row.Attempts >= MaxAttempts)
            {
                _logger.LogWarning("Giving up on the searchable-PDF successor for version {VersionId} after {Attempts} attempts.", source.Id, row.Attempts);
                dbContext.SearchablePdfOutbox.Remove(row);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return row.Attempts >= MaxAttempts; // made "progress" only if we dropped it; otherwise back off
        }

        // Store the PDF under a fresh object key in the SAME document folder as the source (ADR 0530), then create
        // the successor version and remove the outbox row in one commit — so a crash can't produce a duplicate
        // version (an orphan PDF object is harmless).
        var successorVersionId = Guid.NewGuid();
        var pdfKey = ObjectKeyBuilder.SiblingVersionKey(source.ObjectKey, successorVersionId, ".pdf");
        using (var pdfStream = new MemoryStream(pdfBytes))
        {
            await storage.PutObjectAsync(pdfKey, pdfStream, "application/pdf", cancellationToken);
        }

        var sha256Hash = Convert.ToHexStringLower(SHA256.HashData(pdfBytes));

        var nextVersionNumber = 1 + await dbContext.DocumentVersions
            .Where(v => v.DocumentId == source.DocumentId && v.VersionNumber != null)
            .Select(v => v.VersionNumber)
            .MaxAsync(cancellationToken) ?? 1;
        dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = successorVersionId,
            TenantId = source.TenantId,
            DocumentId = source.DocumentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = nextVersionNumber,
            ObjectKey = pdfKey,
            Sha256Hash = sha256Hash,
            DocumentDate = source.DocumentDate,
            CreatedByUserId = source.CreatedByUserId,
            CreatedByServiceAccountId = source.CreatedByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
            SizeBytes = pdfBytes.Length, // storage-quota accounting (ADR "Per-tenant storage quota")
        });

        // Carry the source version's annotations onto the searchable-PDF successor (ADR 0527). The successor
        // becomes the current version, so without this an annotated source (e.g. an imported TIFF) would appear
        // to lose its annotations. The PDF is the same page images with an OCR text layer, so page index +
        // normalized geometry transfer 1:1; author/date/kind/colour/points are preserved.
        var sourceAnnotations = await dbContext.DocumentAnnotations
            .Where(a => a.DocumentVersionId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var a in sourceAnnotations)
        {
            dbContext.DocumentAnnotations.Add(AnnotationCarryOver.ForSuccessorVersion(a, successorVersionId));
        }

        dbContext.SearchablePdfOutbox.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken);
        await storageQuota.AdjustUsageAsync(source.TenantId, pdfBytes.Length, cancellationToken);

        // Re-index so search picks up the successor (its PDF text layer is the OCR'd content).
        await indexQueue.EnqueueAsync(source.DocumentId, cancellationToken);

        _logger.LogInformation("Created searchable-PDF successor version {Version} for document {DocumentId}.", nextVersionNumber, source.DocumentId);
        return true;
    }
}
