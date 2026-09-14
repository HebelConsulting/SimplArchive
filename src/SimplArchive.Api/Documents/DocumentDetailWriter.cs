using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The document detail as ONE value: what the pencil edits (ADR 0278), compared against what is stored, and
/// applied in a single pass so the whole edit commits or none of it does (ADRs 0794/0796).
/// </summary>
/// <remarks>
/// It APPLIES ONLY — it neither gates nor saves nor fires side effects. The caller owns those, which is the
/// entire point: doing each of them ONCE for a save touching several aspects is what the combined endpoint
/// exists for.
///
/// Every aspect delegates to the same writer its own sub-resource uses (<see cref="IndexDataWriter"/>,
/// <see cref="TagSetWriter"/>, <see cref="OcrLanguageWriter"/>, <see cref="MaskAssignment"/>) or is a single
/// assignment. There is deliberately no second implementation of any of them.
/// </remarks>
public class DocumentDetailWriter(
    SimplArchiveDbContext dbContext,
    IndexDataWriter indexData,
    TagSetWriter tagSet,
    OcrLanguageWriter ocrLanguages)
{
    /// <summary>Which aspects a request actually changes — the set the caller gates and audits from.</summary>
    /// <remarks>
    /// A flags enum rather than a list of booleans so the gate can be expressed as one test
    /// (<c>changed &amp; ~Tags</c>), which is what ADR 0796's rule reduces to: tags alone need only
    /// <c>CanEditIndexData</c>; anything else also needs the legal-hold and checked-out checks.
    /// </remarks>
    [Flags]
    public enum Aspect
    {
        None = 0,
        Name = 1 << 0,
        DocumentDate = 1 << 1,
        OcrLanguages = 1 << 2,
        Sensitivity = 1 << 3,
        Tags = 1 << 4,
        IndexData = 1 << 5,
        Mask = 1 << 6,
        ContentsSortOrder = 1 << 7,
    }

    /// <summary>What the apply pass did, for the caller's side effects and its per-aspect audit lines.</summary>
    public sealed record Applied(
        Aspect Changed,
        List<string> Tags,
        DocumentVersion? OcrSourceVersion,
        string? MaskName,
        string? SensitivityLabelName,
        DateOnly? DocumentDate,
        TimeOnly? DocumentTime);

    /// <summary>
    /// Everything the detail holds today, so the caller can answer a GET and so
    /// <see cref="ApplyAsync"/> has something to compare against without reading twice.
    /// </summary>
    public sealed record Snapshot(
        Document Document,
        DocumentVersion? CurrentVersion,
        DocumentVersion? OcrSourceVersion,
        List<string> Tags,
        List<DocumentDetailController.DetailFieldGroup> Fields,
        Guid? MaskId,
        string? MaskName,
        string? SensitivityLabelName,
        bool IsFolder);

    public async Task<Snapshot?> ReadAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var currentVersion = await CurrentVersion.ResolveAsync(
            dbContext.DocumentVersions, documentId, document.CurrentVersionId, cancellationToken);

        // The version OCR would actually convert, which is NOT necessarily the current one — a signed version
        // is excluded, and the newest confirmed TIFF/PDF may sit below a later upload of another kind. Read
        // with the same predicate the writer uses so the value shown and the value written are one answer.
        var ocrSource = await dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed && v.IsSigned != true)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(v => v.ObjectKey.ToLower().EndsWith(".tif") || v.ObjectKey.ToLower().EndsWith(".tiff")
                || v.ObjectKey.ToLower().EndsWith(".pdf"), cancellationToken);

        var tags = await dbContext.DocumentTags
            .Where(t => t.DocumentId == documentId)
            .OrderBy(t => t.Tag)
            .Select(t => t.Tag)
            .ToListAsync(cancellationToken);

        var values = await dbContext.FieldValues
            .Where(v => v.DocumentId == documentId)
            .OrderBy(v => v.Ordinal).ThenBy(v => v.Id)
            .Select(v => new { v.FieldDefinitionId, v.Value })
            .ToListAsync(cancellationToken);

        var fields = values
            .GroupBy(v => v.FieldDefinitionId)
            .Select(g => new DocumentDetailController.DetailFieldGroup
            {
                FieldDefinitionId = g.Key,
                Values = g.Select(v => v.Value).ToList(),
            })
            .OrderBy(f => f.FieldDefinitionId)
            .ToList();

        var mask = document.MaskVersionId is { } maskVersionId
            ? await dbContext.MaskVersions.Where(v => v.Id == maskVersionId)
                .Select(v => new { v.MaskId, v.Name })
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var labelName = document.SensitivityLabelId is { } labelId
            ? await dbContext.SensitivityLabelDefinitions.Where(l => l.Id == labelId)
                .Select(l => l.Name).SingleOrDefaultAsync(cancellationToken)
            : null;

        // A folder is one wearing a folder MASK — the canonical test, rather than "has no versions", which is
        // a consequence that also holds for a document whose upload never finished.
        var isFolder = await FolderMask.IsFolderMaskAsync(dbContext, document.MaskVersionId, cancellationToken);

        return new Snapshot(document, currentVersion, ocrSource, tags, fields, mask?.MaskId, mask?.Name, labelName, isFolder);
    }

    /// <summary>
    /// Compares the request against <paramref name="stored"/> and stages every aspect that differs. Throws the
    /// same refusals each sub-resource throws, because it is the same code that raises them.
    /// </summary>
    public async Task<Applied> ApplyAsync(
        Snapshot stored, DocumentDetailController.SetDetailRequest request, CancellationToken cancellationToken)
    {
        var document = stored.Document;
        var changed = Aspect.None;

        string? maskName = null;
        string? labelName = stored.SensitivityLabelName;
        DocumentVersion? ocrSource = null;
        var tags = stored.Tags;

        // A PUT states the full intended value, so "absent" is not a case: what the caller sent IS the detail,
        // and anything equal to what is stored simply is not a change. That is what makes the gate honest —
        // a request that only re-sends the current values changes nothing and is gated as such.
        if ((request.Name ?? string.Empty) != document.Name && !string.IsNullOrWhiteSpace(request.Name))
        {
            document.Name = request.Name;
            changed |= Aspect.Name;
        }

        if (request.SensitivityLabelId != document.SensitivityLabelId)
        {
            labelName = null;
            if (request.SensitivityLabelId is { } labelId)
            {
                labelName = await dbContext.SensitivityLabelDefinitions
                    .Where(l => l.Id == labelId && l.RetiredAt == null)
                    .Select(l => l.Name)
                    .SingleOrDefaultAsync(cancellationToken);

                if (labelName is null)
                {
                    throw new InvalidSensitivityLabelException();
                }
            }

            document.SensitivityLabelId = request.SensitivityLabelId;
            changed |= Aspect.Sensitivity;
        }

        if (request.ContentsSortOrder is { } sortOrder && sortOrder != document.ContentsSortOrder)
        {
            if (!Enum.IsDefined(sortOrder))
            {
                throw new InvalidContentsSortOrderException();
            }

            document.ContentsSortOrder = sortOrder;
            changed |= Aspect.ContentsSortOrder;
        }

        var (date, time) = ParseDocumentDate(request);
        if (stored.CurrentVersion is { } version && (version.DocumentDate != date || version.DocumentTime != time))
        {
            if (date is { } d)
            {
                version.DocumentDate = d;
                version.DocumentTime = time;
                changed |= Aspect.DocumentDate;
            }
        }

        if (!SameOcrLanguages(request.OcrLanguages, stored.OcrSourceVersion?.OcrLanguages))
        {
            ocrSource = await ocrLanguages.ApplyAsync(document.Id, request.OcrLanguages ?? [], cancellationToken);
            changed |= Aspect.OcrLanguages;
        }

        if (!Normalized(request.Tags).SequenceEqual(stored.Tags))
        {
            tags = await tagSet.ApplyAsync(document, request.Tags, cancellationToken);
            changed |= Aspect.Tags;
        }

        if (!SameFields(request.Fields, stored.Fields))
        {
            await indexData.ApplyAsync(document, ToGroups(request.Fields), request.ConfirmDuplicateClaims, cancellationToken);
            changed |= Aspect.IndexData;
        }

        // LAST, deliberately: assigning a mask validates its required fields, so the values that satisfy them
        // must already be staged. Both clients and DocumentFinalizer.ApplyStagedClassificationAsync order it
        // the same way, and in one transaction the ordering is the only thing that keeps it true.
        if (request.MaskId != stored.MaskId)
        {
            if (request.MaskId is { } maskId)
            {
                var mask = await MaskAssignment.ResolveCurrentVersionAsync(dbContext, maskId, cancellationToken);
                document.MaskVersionId = mask.VersionId;
                maskName = mask.Name;
            }
            else
            {
                document.MaskVersionId = null;
            }

            changed |= Aspect.Mask;
        }

        return new Applied(changed, tags, ocrSource, maskName, labelName, date, time);
    }

    private static (DateOnly? Date, TimeOnly? Time) ParseDocumentDate(DocumentDetailController.SetDetailRequest request)
    {
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(request.DocumentDate))
        {
            if (!DateOnly.TryParse(request.DocumentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                throw new InvalidDocumentDateException($"'{request.DocumentDate}' is not a valid date (expected yyyy-MM-dd).");
            }

            date = parsed;
        }

        TimeOnly? time = null;
        if (!string.IsNullOrWhiteSpace(request.DocumentTime))
        {
            if (!TimeOnly.TryParse(request.DocumentTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                throw new InvalidDocumentTimeException($"'{request.DocumentTime}' is not a valid time (expected HH:mm, 24-hour UTC).");
            }

            time = parsed;
        }

        return (date, time);
    }

    // The same normalization TagSetWriter applies, so "did the tags change?" is asked of the values that would
    // actually be stored rather than of what the caller happened to type.
    private static List<string> Normalized(IReadOnlyList<string>? tags) =>
        (tags ?? [])
            .Select(t => (t ?? string.Empty).Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 100)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    private static bool SameOcrLanguages(IReadOnlyList<string>? requested, string? stored)
    {
        var codes = (requested ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();

        return (codes.Count == 0 ? null : string.Join('+', codes)) == stored;
    }

    private static bool SameFields(
        IReadOnlyList<DocumentDetailController.DetailFieldGroup>? requested,
        IReadOnlyList<DocumentDetailController.DetailFieldGroup> stored)
    {
        var left = (requested ?? [])
            .Where(f => f.Values.Count > 0)
            .OrderBy(f => f.FieldDefinitionId)
            .ToList();

        if (left.Count != stored.Count)
        {
            return false;
        }

        return left.Zip(stored).All(pair =>
            pair.First.FieldDefinitionId == pair.Second.FieldDefinitionId
            && pair.First.Values.SequenceEqual(pair.Second.Values));
    }

    // The index-data writer speaks the sub-resource's own group type, which is what keeps the two paths one
    // implementation rather than two that agree today.
    private static List<DocumentMetadataController.SetFieldValueGroup> ToGroups(
        IReadOnlyList<DocumentDetailController.DetailFieldGroup>? fields) =>
        (fields ?? [])
            .Select(f => new DocumentMetadataController.SetFieldValueGroup
            {
                FieldDefinitionId = f.FieldDefinitionId,
                Values = f.Values,
            })
            .ToList();
}
