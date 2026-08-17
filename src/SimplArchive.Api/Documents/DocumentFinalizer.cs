using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.Documents;

// Confirms a Pending DocumentVersion (server-side hash + version number), auto-classifies the document, and
// files an email's attachments — the shared tail of an upload, used both by DocumentVersionsController's
// finalize and by filing an intray item (ADR "S3-backed inbox"), which arrives with its object already in
// storage. Idempotent: a no-op on an already-Confirmed version.
public class DocumentFinalizer
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IEmailMetadataExtractor _emailMetadataExtractor;
    private readonly IDocumentIndexQueue _queue;
    private readonly ISearchablePdfQueue _searchablePdfQueue;
    private readonly IWormLockService _wormLock;
    private readonly IStorageQuotaService _storageQuota;
    private readonly INotificationService _notifications;
    private readonly ChatSystemEntryRecorder _chatEntries;
    private readonly CalendarContactClassifier _calendarContactClassifier;

    public DocumentFinalizer(
        SimplArchiveDbContext dbContext,
        IObjectStorageClient objectStorageClient,
        IEmailMetadataExtractor emailMetadataExtractor,
        IDocumentIndexQueue queue,
        ISearchablePdfQueue searchablePdfQueue,
        IWormLockService wormLock,
        IStorageQuotaService storageQuota,
        INotificationService notifications,
        ChatSystemEntryRecorder chatEntries,
        CalendarContactClassifier calendarContactClassifier)
    {
        _dbContext = dbContext;
        _objectStorageClient = objectStorageClient;
        _emailMetadataExtractor = emailMetadataExtractor;
        _queue = queue;
        _searchablePdfQueue = searchablePdfQueue;
        _wormLock = wormLock;
        _storageQuota = storageQuota;
        _notifications = notifications;
        _chatEntries = chatEntries;
        _calendarContactClassifier = calendarContactClassifier;
    }

    // Extensions that trigger a searchable-PDF successor job. A TIFF always converts; a PDF is a *candidate* —
    // the worker OCRs it only if it's a scanned image-only document (ADRs "Searchable PDF successor for TIFFs"
    // and "Scanned image-only PDF detection"). The enqueue is a cheap outbox insert; detection is off the
    // request path, so a born-digital PDF just costs a no-op job the worker drops.
    private static readonly HashSet<string> SearchablePdfSourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff", ".pdf" };

    public async Task FinalizeAsync(DocumentVersion version, CancellationToken cancellationToken, StagedClassification? staged = null)
    {
        if (version.Status == DocumentVersionStatus.Confirmed)
        {
            return; // idempotent — see ADR "DocumentVersionsController resource-oriented redesign"
        }

        // Re-fetch and re-hash the object server-side rather than trusting a client-supplied hash.
        //
        // A PDF is buffered rather than streamed, because the same read answers a second question: whether the
        // content carries a digital signature (#491). Examining it HERE costs nothing extra — the bytes are
        // being fetched either way — where doing it later would mean downloading every version again to paint a
        // list. Only PDFs are buffered; nothing else has an in-file signature to find, so everything else keeps
        // streaming straight into the hash.
        string sha256Hash;
        bool? isSigned = null;

        if (Path.GetExtension(version.ObjectKey).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            byte[] content;
            await using (var stream = await _objectStorageClient.GetObjectAsync(version.ObjectKey, cancellationToken))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                content = buffer.ToArray();
            }

            sha256Hash = Convert.ToHexStringLower(SHA256.HashData(content));
            isSigned = DigitalSignature.IsSigned(content);
        }
        else
        {
            await using var stream = await _objectStorageClient.GetObjectAsync(version.ObjectKey, cancellationToken);
            sha256Hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            isSigned = false; // examined, and this format cannot carry one — distinct from "never examined"
        }

        version.IsSigned = isSigned;

        var nextVersionNumber = 1 + await _dbContext.DocumentVersions
            .Where(v => v.DocumentId == version.DocumentId && v.VersionNumber != null)
            .Select(v => v.VersionNumber)
            .MaxAsync(cancellationToken) ?? 1;

        // Storage-quota accounting (ADR "Per-tenant storage quota"): stamp the blob's size and add it to the
        // tenant's maintained counter. The early-return above makes this a no-op on a re-finalize, so the counter
        // isn't double-added. Enforcement (the pre-check) happens at the upload entry point before the blob lands.
        var sizeBytes = await _objectStorageClient.GetObjectSizeAsync(version.ObjectKey, cancellationToken);

        version.Sha256Hash = sha256Hash;
        version.VersionNumber = nextVersionNumber;
        version.Status = DocumentVersionStatus.Confirmed;
        version.SizeBytes = sizeBytes;

        // A newly-confirmed version becomes current: clear any explicit current-version pointer (ADR
        // "Version-restore via a current-version pointer", issue #265) so the document derives its current
        // version as the latest confirmed — this new one — instead of staying pinned to an older restored version.
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == version.DocumentId, cancellationToken);
        if (document is { CurrentVersionId: not null })
        {
            document.CurrentVersionId = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _storageQuota.AdjustUsageAsync(version.TenantId, sizeBytes, cancellationToken);

        // The document's chat thread records that this was filed (ADR 0545). This method is the single point
        // every interactive upload reaches — the versions endpoint, check-in, intray filing and WebDAV all funnel
        // through it — and the early return above makes it idempotent, so a re-finalize can't post twice.
        await _chatEntries.RecordVersionFiledAsync(version, cancellationToken);

        // Classification: a staged intray draft (ADR "Consume the staged mask sidecar at filing") takes over
        // when present — the user classified the item in the intray, so its Name/Document date/mask/index-data
        // are applied and auto-classification is skipped. Otherwise a just-confirmed, still-unclassified
        // document gets its default mask — eMail (fields filled + named after the subject) for .eml/.msg else
        // Basic Entry (ADR "Email auto-classification") — and an email's attachments are filed as child
        // documents (ADR "Email attachments as child documents"). Emails are never staged (they aren't offered
        // a mask in the intray), so a staged draft only ever applies to a non-email.
        if (staged is not null)
        {
            await ApplyStagedClassificationAsync(version, staged, cancellationToken);
        }
        else if (await AutoClassifyAsync(version, cancellationToken))
        {
            await FileEmailAttachmentsAsync(version, cancellationToken);
        }

        // Upload-time default sensitivity label (ADR "Configurable sensitivity labels + upload defaults"): a
        // just-classified, still-unlabelled document inherits its mask's default label, if the mask defines one.
        await ApplyDefaultSensitivityLabelAsync(version.DocumentId, cancellationToken);

        await _queue.EnqueueAsync(version.DocumentId, cancellationToken);

        // A confirmed TIFF (always) or PDF (if the worker finds it's a scan) gets an auto-generated
        // searchable-PDF successor version (ADRs "Searchable PDF successor for TIFFs" and "Scanned image-only
        // PDF detection"). Async: the worker does the detection + OCR off the request path. The original
        // version stays as its own version regardless. The worker-created successor is never re-enqueued (it
        // isn't finalized through here), and its text layer stops it being re-detected as a scan.
        if (SearchablePdfSourceExtensions.Contains(Path.GetExtension(version.ObjectKey)))
        {
            await _searchablePdfQueue.EnqueueAsync(version.DocumentId, version.Id, cancellationToken);
        }

        // Apply WORM Object Lock to the now-confirmed blob per the document's retention/legal-hold state (ADR
        // "WORM / immutable document versions") — after classification, so a just-assigned mask's retention is
        // seen. Best-effort inside the service.
        await _wormLock.ReconcileAsync(version.DocumentId, cancellationToken);

        // Notify everyone following the document — or any ancestor folder (ADR "Folder / subtree subscriptions")
        // — that a version landed. The uploader (the actor) is skipped by the service. Uses the document's
        // current name (post-classification, which may have renamed an email to its subject). A version 1 is a
        // newly-filed document ("new document"); a later version is an update ("new version").
        var documentName = await _dbContext.Documents
            .Where(d => d.Id == version.DocumentId)
            .Select(d => d.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? "a document";
        var isNewDocument = version.VersionNumber == 1;
        await _notifications.NotifyDocumentSubscribersAsync(version.DocumentId, NotificationType.SubscribedActivity,
            isNewDocument ? "New document filed" : "Document updated",
            isNewDocument ? $"'{documentName}' was filed." : $"A new version of '{documentName}' was added.",
            cancellationToken: cancellationToken);
    }

    // Applies an intray item's staged classification draft to the just-confirmed document (ADR "Consume the
    // staged mask sidecar at filing"). Name + Document date always apply; the mask + index-data are applied
    // best-effort — if the staged data fails the mask's required-field / format-range validation, the document
    // is filed WITHOUT the mask (logged via the thrown exception being swallowed) rather than failing the whole
    // filing, since the object has already left the intray. Never auto-classifies (the user took control).
    private async Task ApplyStagedClassificationAsync(DocumentVersion version, StagedClassification staged, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleAsync(d => d.Id == version.DocumentId, cancellationToken);

        // Name (skip a sibling-name collision, same as the email path) + Document date — safe, no mask trigger.
        if (!string.IsNullOrWhiteSpace(staged.Name))
        {
            var newName = staged.Name.Trim();
            var collides = await _dbContext.Documents
                .AnyAsync(d => d.Id != document.Id && d.ParentId == document.ParentId && d.Name == newName, cancellationToken);
            if (!collides)
            {
                document.Name = newName;
            }
        }

        if (DateOnly.TryParse(staged.DocumentDate, out var documentDate))
        {
            version.DocumentDate = documentDate;
        }

        // Staged OCR languages (ADR "Inbox OCR-language staging") — applied to the version before the
        // searchable-PDF conversion is enqueued back in FinalizeAsync, so a scanned TIFF/PDF is OCR'd in the
        // chosen languages. Filtered to the supported catalog (best-effort; unknown codes are dropped).
        if (!string.IsNullOrWhiteSpace(staged.OcrLanguages))
        {
            var known = OcrLanguages.Supported.Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
            var valid = staged.OcrLanguages
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(known.Contains)
                .ToList();
            if (valid.Count > 0)
            {
                version.OcrLanguages = string.Join("+", valid);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (staged.MaskId is not { } maskId)
        {
            return; // "(No mask)" staged — nothing more to apply.
        }

        // Fill index data first, then assign the mask (which triggers required-field validation — the values
        // must already be present), mirroring the index-data endpoint's order.
        var added = new List<FieldValue>();
        foreach (var (fieldDefinitionId, values) in staged.Fields)
        {
            foreach (var value in values)
            {
                var fieldValue = new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = document.TenantId,
                    DocumentId = document.Id,
                    FieldDefinitionId = fieldDefinitionId,
                    Value = value,
                };
                _dbContext.FieldValues.Add(fieldValue);
                added.Add(fieldValue);
            }
        }

        try
        {
            document.MaskVersionId = await ResolveCurrentMaskVersionIdAsync(maskId, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort: the staged mask/index-data didn't validate (required/format/range) or the mask is
            // gone. Revert the attempted change so the document is simply filed without a mask.
            foreach (var fieldValue in added)
            {
                _dbContext.Entry(fieldValue).State = EntityState.Detached;
            }

            document.MaskVersionId = null;
            _dbContext.Entry(document).State = EntityState.Unchanged;
        }
    }

    // Returns true if the document was classified as an email (the caller then files its attachments).
    // Applies the assigned mask's upload-time default sensitivity label to a document that has none yet (ADR
    // "Configurable sensitivity labels + upload defaults"). A no-op if the document is already labelled, has no
    // mask, or the mask defines no default.
    private async Task ApplyDefaultSensitivityLabelAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleAsync(d => d.Id == documentId, cancellationToken);
        if (document.SensitivityLabelId is not null || document.MaskVersionId is null)
        {
            return;
        }

        var defaultLabelId = await _dbContext.MaskVersions
            .Where(m => m.Id == document.MaskVersionId)
            .Select(m => m.DefaultSensitivityLabelId)
            .FirstOrDefaultAsync(cancellationToken);
        if (defaultLabelId is { } labelId)
        {
            document.SensitivityLabelId = labelId;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> AutoClassifyAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleAsync(d => d.Id == version.DocumentId, cancellationToken);

        // Already classified with a real (non-Folder) mask → leave it. A document created as a folder carries the
        // Folder mask (ADR "Folder mask on folders"); once it gets a version it's a leaf, so treat Folder-or-null
        // as "still unclassified" and reclassify to Basic Entry / eMail.
        if (document.MaskVersionId is not null && !await FolderMask.IsFolderMaskAsync(_dbContext, document.MaskVersionId, cancellationToken))
        {
            return false;
        }

        // Derive the type from the object key, not Document.Name — the name no longer carries the extension
        // (ADR "Extension off Document.Name, derived from the object key").
        var extension = Path.GetExtension(version.ObjectKey).ToLowerInvariant();

        if (extension is ".eml" or ".msg")
        {
            EmailMetadata? metadata = null;
            try
            {
                await using var stream = await _objectStorageClient.GetObjectAsync(version.ObjectKey, cancellationToken);
                metadata = await _emailMetadataExtractor.ExtractAsync(stream, extension, cancellationToken);
            }
            catch (Exception)
            {
                // Fall through to the default mask if the object can't be read/parsed.
            }

            if (metadata is not null)
            {
                await ClassifyAsEmailAsync(document, version, metadata, cancellationToken);
                return true;
            }
        }

        // A .vcf/.ics becomes a Contact/Calendar (#564, ADR 0619) — required, not decorative: the typed-folder
        // containment invariant refuses a Basic-Entry-masked child of a Contact/Calendar, so without
        // this an upload into one of those folders could not be saved at all.
        if (CalendarContactClassifier.Handles(extension)
            && await _calendarContactClassifier.TryClassifyAsync(document, version, cancellationToken))
        {
            return false; // classified, but it has no attachments to file
        }

        document.MaskVersionId = await ResolveCurrentMaskVersionIdAsync(WellKnownMaskIds.BasicEntry, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return false;
    }

    // One level only (a nested email's own attachments aren't extracted); best-effort per attachment. The
    // attachment children inherit the email version's creator.
    private async Task FileEmailAttachmentsAsync(DocumentVersion emailVersion, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(emailVersion.ObjectKey).ToLowerInvariant();

        IReadOnlyList<EmailAttachment> attachments;
        try
        {
            await using var stream = await _objectStorageClient.GetObjectAsync(emailVersion.ObjectKey, cancellationToken);
            attachments = await _emailMetadataExtractor.ExtractAttachmentsAsync(stream, extension, cancellationToken);
        }
        catch (Exception)
        {
            return;
        }

        if (attachments.Count == 0)
        {
            return;
        }

        var email = await _dbContext.Documents.SingleAsync(d => d.Id == emailVersion.DocumentId, cancellationToken);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attachment in attachments)
        {
            try
            {
                await FileAttachmentAsync(email, emailVersion.CreatedByUserId, emailVersion.CreatedByServiceAccountId, attachment, usedNames, cancellationToken);
            }
            catch (Exception)
            {
                // Best-effort: skip an attachment that can't be stored, keep filing the rest.
            }
        }
    }

    private async Task FileAttachmentAsync(
        Document email, Guid? createdByUserId, Guid? createdByServiceAccountId,
        EmailAttachment attachment, HashSet<string> usedNames, CancellationToken cancellationToken)
    {
        // Split the attachment filename: the extension goes on the object key, the stem becomes Document.Name
        // (ADR "Extension off Document.Name, derived from the object key").
        var sanitized = SanitizeAttachmentName(attachment.FileName);
        var extension = Path.GetExtension(sanitized);
        var name = Disambiguate(Path.GetFileNameWithoutExtension(sanitized), usedNames);
        usedNames.Add(name);

        var childId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // The key groups by the new child document (ADR 0530): its filing year + a fresh storage folder, version id leaf.
        var storageFolderId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(email.TenantId, now, storageFolderId, versionId, extension);

        await using (var content = new MemoryStream(attachment.Content))
        {
            await _objectStorageClient.PutObjectAsync(objectKey, content, attachment.ContentType ?? "application/octet-stream", cancellationToken);
        }

        var child = new Document
        {
            Id = childId,
            TenantId = email.TenantId,
            ParentId = email.Id,
            Name = name,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = now,
            StorageFolderId = storageFolderId,
        };

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = email.TenantId,
            DocumentId = childId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(attachment.Content)),
            ObjectKey = objectKey,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
            SizeBytes = attachment.Content.Length, // storage-quota accounting (ADR "Per-tenant storage quota")
        };

        _dbContext.Documents.Add(child);
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _storageQuota.AdjustUsageAsync(email.TenantId, attachment.Content.Length, cancellationToken);

        await AutoClassifyAsync(version, cancellationToken); // one level — don't recurse into its attachments
        await _queue.EnqueueAsync(childId, cancellationToken);
    }

    private async Task ClassifyAsEmailAsync(Document document, DocumentVersion version, EmailMetadata metadata, CancellationToken cancellationToken)
    {
        var maskVersionId = await ResolveCurrentMaskVersionIdAsync(WellKnownMaskIds.EMail, cancellationToken);

        var fieldIdsByName = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId)
            .Select(f => new { f.Name, f.Id })
            .ToDictionaryAsync(f => f.Name, f => f.Id, cancellationToken);

        void AddValue(string fieldName, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !fieldIdsByName.TryGetValue(fieldName, out var fieldDefinitionId))
            {
                return;
            }

            _dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = fieldDefinitionId,
                Value = value,
            });
        }

        AddValue("From", string.IsNullOrWhiteSpace(metadata.From) ? "(unknown)" : metadata.From);
        AddValue("To", string.IsNullOrWhiteSpace(metadata.To) ? "(unknown)" : metadata.To);
        AddValue("Subject", string.IsNullOrWhiteSpace(metadata.Subject) ? "(unknown)" : metadata.Subject);
        AddValue("Cc", metadata.Cc);
        AddValue("Entry ID", metadata.MessageId);

        if (metadata.Date is { } date)
        {
            AddValue("Date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            version.DocumentDate = DateOnly.FromDateTime(date.UtcDateTime);
        }

        if (!string.IsNullOrWhiteSpace(metadata.Subject))
        {
            var subject = metadata.Subject.Trim();
            var collides = await _dbContext.Documents
                .AnyAsync(d => d.Id != document.Id && d.ParentId == document.ParentId && d.Name == subject, cancellationToken);
            if (!collides)
            {
                document.Name = subject;
            }
        }

        document.MaskVersionId = maskVersionId;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> ResolveCurrentMaskVersionIdAsync(Guid maskId, CancellationToken cancellationToken)
    {
        return await _dbContext.MaskVersions
            .Where(v => v.MaskId == maskId && v.IsCurrent)
            .Select(v => v.Id)
            .SingleAsync(cancellationToken);
    }

    private static string SanitizeAttachmentName(string? fileName)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }

    private static string Disambiguate(string name, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(name))
        {
            return name;
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i}){extension}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

// An intray item's staged classification draft (from its `{name}.mask.json` sidecar), applied at filing time
// (ADR "Consume the staged mask sidecar at filing"). DocumentDate is a "yyyy-MM-dd" string; MaskId null = none.
public sealed record StagedClassification(
    string? Name, string? DocumentDate, Guid? MaskId,
    IReadOnlyList<(Guid FieldDefinitionId, IReadOnlyList<string> Values)> Fields,
    // Staged OCR languages ("+"-joined Tesseract codes) — set on the version before the searchable-PDF
    // conversion is enqueued (ADR "Inbox OCR-language staging"). Null = the tenant default.
    string? OcrLanguages = null);
