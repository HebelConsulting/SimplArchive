using System;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The deep-link address forms (#761): what "Copy link" produces and what "Go to link…" / the
/// <c>simplarchive://</c> scheme handler accept. Pure, so every accepted shape is pinned in a unit test.
/// </summary>
/// <remarks>
/// What CIRCULATES is always the https web-app URL (<c>{server}/go/{id}</c>) — universally openable by any
/// recipient in a browser, whichever client the sender used. The <c>simplarchive://go/{id}</c> scheme form is
/// additionally accepted (and registered with the OS) so a link rewritten to it opens the desktop client
/// natively; the parser takes both, because a paste box that rejects the app's own scheme would be absurd.
/// </remarks>
public static class DeepLinks
{
    public const string Scheme = "simplarchive";

    /// <summary>The link "Copy link" puts on the clipboard: the web app's /go route on this client's server.</summary>
    public static string BuildLink(string apiBaseUrl, Guid documentId) =>
        $"{apiBaseUrl.TrimEnd('/')}/go/{documentId}";

    /// <summary>The document id carried by a pasted or scheme-launched link, or null when the text is neither
    /// form. Accepts <c>http(s)://…/go/{id}</c> and <c>simplarchive://go/{id}</c> (host or first segment).</summary>
    public static Guid? ParseDocumentId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        // simplarchive://go/{id} parses with Host "go" and the id as the path; a tolerant variant with an
        // extra slash (simplarchive:///go/{id}) carries both in the path instead. Normalise to segments.
        var segments = ((uri.Host is { Length: > 0 } host && !host.Contains('.') && uri.Scheme == Scheme ? host + uri.AbsolutePath : uri.AbsolutePath))
            .Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var isWeb = uri.Scheme is "http" or "https";
        var isScheme = uri.Scheme == Scheme;
        if (!isWeb && !isScheme)
        {
            return null;
        }

        return segments.Length >= 2 && segments[^2].Equals("go", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[^1], out var id)
            ? id
            : null;
    }
}
