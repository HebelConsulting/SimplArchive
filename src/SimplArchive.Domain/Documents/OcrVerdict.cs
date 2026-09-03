namespace SimplArchive.Domain.Documents;

/// <summary>
/// What the scanned-PDF detector concluded about a version (#999) — persisted instead of discarded, so the
/// clients can gate the OCR affordances on a stored fact and say WHY the automatic path did or did not run
/// (ADR 0626's principle moved from the logs into the UI).
/// </summary>
/// <remarks>
/// Null on <see cref="DocumentVersion.OcrVerdict"/> means "not yet judged": the worker judges a PDF when its
/// outbox row is processed, seconds after filing; a version that predates the column stays null until its
/// next enqueue. TIFFs are trivially convertible and get their verdict at finalize.
/// </remarks>
public enum OcrVerdict
{
    /// <summary>An image-only scan — the automatic successor pipeline converts it.</summary>
    ConvertibleScan,

    /// <summary>Deliberately not a candidate: has a text layer, no bitmap, or is signed.</summary>
    NotAScan,

    /// <summary>Could not be read (encrypted, corrupt, beyond the parser) — left alone, and said out loud.</summary>
    Unreadable,
}
