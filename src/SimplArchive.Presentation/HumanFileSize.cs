using System.Globalization;

namespace SimplArchive.Presentation;

/// <summary>
/// A byte count as a person reads it, formatted identically wherever a document's size is shown.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the size is one of the rows the index-data pane and IMAP must present
/// the same way (<see cref="DocumentDetailRows"/>). Two copies of a rounding rule is exactly how "1.2 MB" on
/// one surface becomes "1.18 MB" on the other, which is a disagreement a reader cannot explain and nobody
/// would think to test.
///
/// Invariant culture on purpose: the label beside it is translated, the number is not. A decimal comma here
/// would also land inside a MIME header value, which is not a place to discover a locale.
/// </remarks>
public static class HumanFileSize
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
    };
}
