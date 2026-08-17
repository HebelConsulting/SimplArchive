using System.Net.Http.Json;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>The OCR languages the tenant offers, fetched once and shared by everything that picks them.</summary>
/// <remarks>
/// <para>
/// Three surfaces need the same list: the Repositories detail pane (per-version languages), the Intray staging
/// form (staged before filing), and the Tenant tab (the tenant-wide default). While the workbench was one page
/// they shared a field. The Intray extraction gave that tab its own lazily-loaded copy — defensible for one — and
/// the Tenant tab would have made a **third**, which is the point at which CLAUDE.md says to stop copying and
/// write it once. The sensitivity labels reached the same shape one tab earlier, for the same reason.
/// </para>
/// <para>
/// Fetched on first use rather than at startup: a session that never opens a scannable document, the intray, or
/// tenant settings should not pay for it at all.
/// </para>
/// </remarks>
public sealed class OcrLanguageCatalog(HttpClient http, ApiRoot apiRoot)
{
    private List<OcrLanguageOption>? _languages;

    /// <summary>The languages, fetching them on first use. Empty on failure — a picker with no options.</summary>
    public async Task<IReadOnlyList<OcrLanguageOption>> GetAsync()
    {
        if (_languages is not null)
        {
            return _languages;
        }

        try
        {
            var resp = await http.GetFromJsonAsync<OcrCatalogResponse>(await apiRoot.RequireAsync("ocrLanguages"));
            _languages = resp?.Languages ?? [];
        }
        catch (Exception)
        {
            _languages = [];
        }

        return _languages;
    }

    /// <summary>The display names for a set of codes, for showing a selection back to the user.</summary>
    public string Describe(IReadOnlyList<string> codes, string emptyText) => codes.Count == 0
        ? emptyText
        : string.Join(", ", codes.Select(c => _languages?.FirstOrDefault(o => o.Code == c)?.DisplayName ?? c));
}
