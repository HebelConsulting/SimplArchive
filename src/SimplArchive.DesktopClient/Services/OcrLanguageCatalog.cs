using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The server's OCR language list: fetched once per session, and used to turn stored codes into display names.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because it was <b>three copies</b> of the same lazy-load — in the Intray's staging editor, the
/// tenant settings pane and the document detail pane — plus one shared <c>DescribeOcrLanguages</c> helper with
/// eight call sites. The three agreed only because nobody had changed one: they already differed in their catch
/// clause (<c>catch</c> versus <c>catch (Exception)</c>) and in all three comments. That is the standing rule
/// about N copies, met at the third occurrence rather than the fourth.
/// </para>
/// <para>
/// It is a service rather than view-model state (#517) because nothing binds to it: callers ask it for a
/// string. A failed fetch deliberately leaves the catalogue empty rather than throwing — <see cref="Describe"/>
/// then falls back to the raw codes, which is more use to a reader than an error, and is what all three copies
/// already did.
/// </para>
/// </remarks>
public sealed class OcrLanguageCatalog(SimplArchiveApiClient api)
{
    private IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> _options = [];

    /// <summary>The catalogue as fetched — empty until <see cref="EnsureLoadedAsync"/> succeeds.</summary>
    public IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> Options => _options;

    /// <summary>Fetches the catalogue once. Best-effort: a failure leaves it empty, exactly as before.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_options.Count > 0)
        {
            return;
        }

        try
        {
            _options = await api.GetOcrLanguageCatalogAsync();
        }
        catch (Exception)
        {
            // Non-fatal: the picker shows codes instead of names. The broadest of the three original catches,
            // so no caller loses a case it used to survive.
        }
    }

    /// <summary>
    /// The codes as a readable, priority-ordered display ("German, French"), falling back to the code itself
    /// for one the catalogue lacks. Empty means the tenant's own setting — the wording is the former helper's,
    /// kept verbatim because eight call sites put it on screen.
    /// </summary>
    public string Describe(IReadOnlyList<string> codes) =>
        codes.Count == 0
            ? "(tenant default)"
            : string.Join(", ", codes.Select(c => _options.FirstOrDefault(o => o.Code == c)?.DisplayName ?? c));
}
