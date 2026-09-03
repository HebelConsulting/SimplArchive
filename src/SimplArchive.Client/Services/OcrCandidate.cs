using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// Which of a document's versions is the OCR source, and what travels with it (#999) — ONE derivation for
/// every pane that answers it (the Repositories detail via <see cref="DetailLoader"/>, the Check-out
/// detail), because two copies of "what is a candidate" is how the panes would come to disagree about the
/// same document. The server's rel emission shares the predicate.
/// </summary>
/// <param name="Verdict">The persisted detector verdict, null while unjudged.</param>
/// <param name="OcrCodes">The candidate's language override, split for the chips.</param>
/// <param name="MakeSearchableHref">The candidate's make-searchable rel, when advertised (ADR 0543).</param>
public sealed record OcrCandidate(string? Verdict, List<string> OcrCodes, string? MakeSearchableHref)
{
    /// <summary>The latest confirmed, unsigned TIFF-or-PDF version, or null when none qualifies.</summary>
    public static OcrCandidate? From(IEnumerable<VersionResponse> confirmed)
    {
        static bool IsCandidate(string? key) => key is not null
            && (key.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        var source = confirmed
            .Where(v => IsCandidate(v.ObjectKey) && !v.IsSigned)
            .OrderByDescending(v => v.VersionNumber ?? 0)
            .FirstOrDefault();
        if (source is null)
        {
            return null;
        }

        return new OcrCandidate(
            source.OcrVerdict,
            string.IsNullOrWhiteSpace(source.OcrLanguages)
                ? []
                : source.OcrLanguages.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList(),
            source.Links.FirstOrDefault(l => l.Rel == "make-searchable")?.Href);
    }
}
