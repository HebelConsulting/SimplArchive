namespace SimplArchive.Application.Abstractions;

// Extracts both sides of a comparison to plain text (ADR 0712, superseding the server-side diff of ADR
// "Document version comparison"): plain-text formats decode directly, emails go through MimeKit, office/PDF
// through ITextExtractor (Tika) when configured. The DIFF itself — row alignment, word emphasis — is computed
// by the clients from these texts, in the shared SimplArchive.Presentation.TextDiff, so both clients answer
// "what changed" identically and a side-by-side view needs no second wire shape.
// Available is false when either side has no extractable text (a binary/image format, or Tika unavailable).
public interface IDocumentVersionComparer
{
    // toExtensionHint: when the "to" object key carries no file extension (e.g. the extensionless check-out stash
    // key, ADR 0517), the format to treat it as for the direct text-decode path — so a text-file compare doesn't
    // fall back to Tika. Ignored when the key already has an extension. The "from" key always carries its own.
    Task<VersionComparison> CompareAsync(string fromObjectKey, string toObjectKey, string? toExtensionHint = null, CancellationToken cancellationToken = default);
}

/// <summary>The two texts, or why there are none (the clients say WHY, not just "unavailable").</summary>
public sealed record VersionComparison(bool Available, string FromText, string ToText,
    ComparisonUnavailable Reason = ComparisonUnavailable.None);

/// <summary>Why a comparison has no texts. Distinct because the remedies are: a format with no text has none, while a
/// format that needs text extraction compares fine on an installation that runs it (it was one message, #bug).</summary>
public enum ComparisonUnavailable
{
    None,
    NoText,
    NoTextExtraction,
}
