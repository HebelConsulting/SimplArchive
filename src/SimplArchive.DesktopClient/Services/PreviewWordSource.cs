namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Where the find-in-document word boxes come from: the server's text layout, or — when it will not answer —
/// the PDF bytes this client already holds (#1402).
/// </summary>
/// <remarks>
/// <para>
/// Its own class rather than three methods on <c>PreviewViewModel</c>, because it is a POLICY with two sources
/// and a precedence, and because the view-model had reached the 1000-line limit CLAUDE.md sets. Extracting it
/// also makes the precedence testable without standing up a preview.
/// </para>
/// <para>
/// <b>The server first, and locally only as a fallback.</b> The server's layout is cached, and for an IMAGE it
/// is the only possible source — it comes from OCR, which this client does not have. So local extraction never
/// replaces it; it answers the case where the server cannot.
/// </para>
/// <para>
/// <b>Which is the strict tier's case, and is why this exists.</b> There the text-layout endpoint refuses
/// outright — <c>409 PLAINTEXT_CONTENT_REFUSED</c> (ADR 0829), because every word with its coordinates
/// reconstructs the document through a route that presigns nothing — so the words have to be computed where the
/// document is in the clear, which is here, after the card opened the envelope (ADR 0830). The extraction itself
/// is SHARED with the server (<c>SimplArchive.TextLayout</c>) rather than reimplemented, so a strict reader's
/// overlay agrees with everybody else's instead of drifting on a Y-flip or a punctuation rule.
/// </para>
/// <para>
/// A scanned page yields nothing, and that is the honest limit: OCR is what would fill it. The caller leaves the
/// find affordance off rather than offering a search that silently matches nothing.
/// </para>
/// </remarks>
internal static class PreviewWordSource
{
    /// <summary>One list of boxes per page, in page order; empty when no source could produce any.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<VersionsClient.TextLayoutBox>>> LoadAsync(
        SimplArchiveApiClient? api, string? textLayoutUrl, byte[]? pdfBytes)
    {
        if (api is not null && textLayoutUrl is not null)
        {
            try
            {
                if (await api.Versions.GetTextLayoutAsync(textLayoutUrl) is { Pages.Count: > 0 } layout)
                {
                    return layout.Pages.Select(p => p.Words).ToList();
                }
            }
            catch (Exception)
            {
                // Best-effort, and deliberately quiet: a strict tenant REFUSES this by design, and an ordinary
                // one can simply have no rendition to read from. Neither is worth a dialog when the answer may
                // be computable below.
            }
        }

        if (pdfBytes is null)
        {
            return [];
        }

        try
        {
            var local = await Task.Run(() => SimplArchive.TextLayout.PdfWordBoxes.Read(pdfBytes));
            return local.Pages
                .Select(page => (IReadOnlyList<VersionsClient.TextLayoutBox>)page.Words
                    .Select(w => new VersionsClient.TextLayoutBox(w.Text, w.X, w.Y, w.Width, w.Height))
                    .ToList())
                .ToList();
        }
        catch (Exception)
        {
            // A PDF this parser cannot read is not a reason to lose the preview that already rendered.
            return [];
        }
    }
}
