using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// The detail pane's system fields (#999 split this out by responsibility — the main file crossed the
// 1000-line rule when the OCR-candidate walk widened): the current version's read-only facts, the OCR
// candidate + its persisted verdict + the make-searchable rel, and the OCR-language override write.
public sealed partial class DocumentsClient
{
    // System-field values shown always (separate from the mask, ADR "System fields + OCR-language mask
    // field"). Created/CreatedBy/DocumentDate are the currently-shown version's; the OCR-language override +
    // TIFF-source come from the latest TIFF version.
    // DocumentDateHref is the current version's own `document-date` address — the detail pane's Save follows it
    // instead of rebuilding a path out of the two ids beside it (ADR 0543, issue #416).
    public sealed record SystemFields(
        Guid CurrentVersionId, int CurrentVersionNumber, DateTimeOffset CreatedAt, string CreatedByName, string DocumentDate,
        // Renamed from HasTiffVersion with the #999 widening: an OCR candidate is any unsigned TIFF or PDF —
        // a flag named for TIFFs that also means PDFs is the next reader's trap (the issue's own warning).
        bool IsOcrCandidate, string? OcrLanguages, string FileExtension, string? DocumentDateHref = null, string? WorkflowStatus = null,
        // The candidate version's persisted verdict (null while unjudged) and its make-searchable rel (#999).
        string? OcrVerdict = null, string? MakeSearchableHref = null);

    public async Task<SystemFields?> GetSystemFieldsAsync(string versionsHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(versionsHref, cancellationToken);
        if (VersionsClient.PickCurrentVersionElement(response) is not { } picked)
        {
            return null;
        }

        var cur = picked.Version;
        var currentNumber = picked.Number;

        // The latest OCR-candidate version — the OCR source, a separate concept from "current". Any
        // unsigned TIFF or PDF since #999 (the TIFF-only rule predated scanned-PDF support, and its copy
        // here is what kept the selector off exactly the documents OCR exists for).
        JsonElement? tiff = null;
        var tiffNumber = -1;
        if (response.TryGetProperty("versions", out var versions))
        {
            foreach (var v in versions.EnumerateArray())
            {
                if (v.GetProperty("status").GetString() != "Confirmed")
                {
                    continue;
                }

                if (v.TryGetProperty("isSigned", out var signed) && signed.ValueKind == JsonValueKind.True)
                {
                    continue; // OCR would break the signature — no affordance for a signed version.
                }

                var number = v.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number ? vn.GetInt32() : 0;
                var objectKey = v.TryGetProperty("objectKey", out var ok) ? ok.GetString() ?? "" : "";
                var isCandidate = objectKey.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                    || objectKey.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
                    || objectKey.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                if (isCandidate && number >= tiffNumber)
                {
                    tiffNumber = number;
                    tiff = v;
                }
            }
        }

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";

        string? ocr = null;
        if (tiff is { } t && t.TryGetProperty("ocrLanguages", out var o) && o.ValueKind == JsonValueKind.String)
        {
            ocr = o.GetString();
        }

        return new SystemFields(
            cur.GetProperty("id").GetGuid(),
            currentNumber,
            cur.TryGetProperty("createdAt", out var ca) ? ca.GetDateTimeOffset() : default,
            Str(cur, "createdByName"),
            Str(cur, "documentDate"),
            tiff is not null,
            ocr,
            Str(cur, "fileExtension"),
            ApiCore.RelHref(cur, "document-date"), SimplArchiveApiClient.StrOrNull(cur, "workflowStatus"),
            tiff is { } tv ? SimplArchiveApiClient.StrOrNull(tv, "ocrVerdict") : null,
            tiff is { } tl ? ApiCore.RelHref(tl, "make-searchable") : null);
    }

    // Sets the document's OCR-language override (ordered codes) and re-runs the searchable-PDF conversion.
    public async Task SetOcrLanguagesAsync(string ocrLanguagesHref, IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(ocrLanguagesHref, new { languages = codes }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set OCR languages ({(int)response.StatusCode}).");
        }
    }
}
