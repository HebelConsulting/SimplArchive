using System.Text;
using MimeKit;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Comparison;

// Extracts two versions' blobs to plain text for the client-side diff (ADR 0712). Text formats decode directly;
// emails parse via MimeKit (a note version is HTML in an .eml — the text body when there is one, else the HTML
// body stripped to text); everything else goes through Tika. Available is false when either side yields no text
// (binary/image, or Tika not configured).
public sealed class DocumentVersionComparer : IDocumentVersionComparer
{
    // Formats decoded straight to UTF-8 text — no Tika needed (so comparison works on a Tika-less deployment).
    // .html/.htm stay verbatim deliberately: a stored HTML FILE diffs as its source; only an email's HTML-only
    // body is stripped, because there the markup is a mail client's encoding of prose, not the user's document.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".log", ".html", ".htm", ".yml", ".yaml", ".rtf", ".tsv",
    };

    // .msg deliberately not here: MimeKit does not read the Outlook container format (that path uses
    // MSGReader, in EmailConverter) — a .msg falls through to Tika like any other opaque format.

    private readonly IObjectStorageClient _objectStorage;
    private readonly ITextExtractor _textExtractor;

    public DocumentVersionComparer(IObjectStorageClient objectStorage, ITextExtractor textExtractor)
    {
        _objectStorage = objectStorage;
        _textExtractor = textExtractor;
    }

    public async Task<VersionComparison> CompareAsync(string fromObjectKey, string toObjectKey, string? toExtensionHint = null, CancellationToken cancellationToken = default)
    {
        var fromText = await ExtractTextAsync(fromObjectKey, null, cancellationToken);
        var toText = await ExtractTextAsync(toObjectKey, toExtensionHint, cancellationToken);

        return fromText is null || toText is null
            ? new VersionComparison(false, string.Empty, string.Empty)
            : new VersionComparison(true, fromText, toText);
    }

    // The version's text, or null when it can't be extracted (binary/image, or Tika unavailable → "").
    // extensionHint supplies the format when objectKey has no extension of its own (the check-out stash, ADR 0517).
    private async Task<string?> ExtractTextAsync(string objectKey, string? extensionHint, CancellationToken cancellationToken)
    {
        await using var stream = await _objectStorage.GetObjectAsync(objectKey, cancellationToken);

        // A known text format: decode the bytes directly (reliable without Tika). Prefer the key's own extension;
        // fall back to the caller's hint for an extensionless key.
        var extension = Path.GetExtension(objectKey) is { Length: > 0 } ext ? ext : extensionHint ?? string.Empty;
        if (TextExtensions.Contains(extension))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        // An email: the bodies are what a user means by "the text", not the MIME envelope — and a note edited
        // from a mail client is exactly this case (#803). MimeKit is deterministic and needs no sidecar.
        if (extension.Equals(".eml", StringComparison.OrdinalIgnoreCase))
        {
            var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            var body = message.TextBody;
            if (string.IsNullOrWhiteSpace(body) && message.HtmlBody is { } html)
            {
                body = HtmlText.Strip(html);
            }

            return string.IsNullOrWhiteSpace(body) ? null : body;
        }

        // Otherwise route through the text extractor (Tika) — "" means unsupported / not configured.
        var text = await _textExtractor.ExtractAsync(stream, "application/octet-stream", cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
