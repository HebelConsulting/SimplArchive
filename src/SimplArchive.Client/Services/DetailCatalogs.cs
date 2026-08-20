using System.Net.Http.Json;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// The four tenant-wide lists the index-data pane needs — masks, OCR languages, sensitivity labels and the tag
/// catalogue — behind one reader, so nothing has to hold a copy of them to render.
/// </summary>
/// <remarks>
/// <para>
/// They were page fields, loaded inside <c>BeginEditAsync</c> and passed down as four parameters. That put them
/// in the wrong place twice over: they are not edit state (read-mode chips are coloured from the tag catalogue,
/// and the sensitivity list is fetched again when a row is merely selected), and each carried its own
/// <c>if (count == 0) load</c> guard at every use — the "load it once" decision restated per call site rather
/// than made once here.
/// </para>
/// <para>
/// Two of the four already had a shared owner (<see cref="SensitivityLabelCatalog"/>,
/// <see cref="OcrLanguageCatalog"/>), and this composes them rather than re-fetching: the Users &amp; groups and
/// Tenant tabs keep reading those directly, and everyone sees the same list. Only the masks and the tag
/// catalogue are fetched here, because nothing else reads them.
/// </para>
/// <para>
/// Scoped, which in a WebAssembly host is the app's lifetime — so a catalogue survives a tab switch that
/// disposes the pane, and the second visit to a document costs no requests at all.
/// </para>
/// </remarks>
public sealed class DetailCatalogs(
    HttpClient http,
    ApiRoot apiRoot,
    SensitivityLabelCatalog sensitivityLabels,
    OcrLanguageCatalog ocrLanguages)
{
    private static readonly Dictionary<string, string?> NoColors = [];

    /// <summary>The tenant's masks, for the pane's mask picker.</summary>
    public IReadOnlyList<MaskSummary> Masks { get; private set; } = [];

    /// <summary>The OCR languages offered for a scannable version (ADR 0272).</summary>
    public IReadOnlyList<OcrLanguageOption> Ocr { get; private set; } = [];

    /// <summary>The tenant's sensitivity labels (ADR "Configurable sensitivity labels").</summary>
    public IReadOnlyList<SensitivityLabel> Sensitivity { get; private set; } = [];

    /// <summary>Tag names for the add-box autocomplete.</summary>
    public IReadOnlyList<string> TagNames { get; private set; } = [];

    private IReadOnlyDictionary<string, string?> _tagColors = NoColors;

    /// <summary>A tag's configured chip colour, or <c>null</c> for the default — used in both read and edit mode.</summary>
    public string? TagColor(string name) => _tagColors.TryGetValue(name, out var c) ? c : null;

    /// <summary>
    /// The sensitivity labels, fetched on first use. Called when a row is selected, because the pane shows the
    /// label whether or not anyone edits it.
    /// </summary>
    public async Task EnsureSensitivityAsync()
    {
        if (Sensitivity.Count == 0)
        {
            Sensitivity = await sensitivityLabels.GetAsync();
        }
    }

    /// <summary>
    /// Re-reads the sensitivity labels after the admin dialog has edited them. The dialog invalidates the shared
    /// catalogue on close, so this picks up the new list rather than the cached one.
    /// </summary>
    public async Task ReloadSensitivityAsync() => Sensitivity = await sensitivityLabels.GetAsync();

    /// <summary>
    /// Everything an open edit needs. <paramref name="needsOcr"/> is the version's own answer — a non-TIFF never
    /// offers the picker, so it should not pay for the list (the same reason the shared catalogues fetch lazily).
    /// </summary>
    public async Task EnsureForEditAsync(bool needsOcr)
    {
        await EnsureTagsAsync();
        await EnsureSensitivityAsync();
        // Only masks a user may actually CHOOSE (#671). A folder mask types a folder and an extension-claimed
        // mask is assigned by the classifier on upload, so offering either is offering a refusal that the
        // containment invariant delivers after the save rather than before it (#580). The server decides, and
        // both clients read the same flag — they used to derive it, differently.
        Masks = [.. ((await http.GetFromJsonAsync<MaskListResponse>(await apiRoot.RequireAsync("masks")))?.Masks ?? [])
            .Where(m => m.IsFreelyAssignable)];
        if (needsOcr && Ocr.Count == 0)
        {
            Ocr = await ocrLanguages.GetAsync();
        }
    }

    /// <summary>The tag catalogue, fetched on first use.</summary>
    public async Task EnsureTagsAsync()
    {
        if (TagNames.Count == 0)
        {
            await ReloadTagsAsync();
        }
    }

    /// <summary>
    /// Re-reads the tag catalogue. The Tags tab raises this after an edit, so a colour changed there shows on the
    /// next chip the shell renders. Best-effort: autocomplete falls back to free text and chips render uncoloured.
    /// </summary>
    public async Task ReloadTagsAsync()
    {
        try
        {
            var catalog = (await http.GetFromJsonAsync<TagsResponse>(await apiRoot.RequireAsync("tags")))?.Catalog ?? [];
            TagNames = catalog.Select(t => t.Name).ToList();
            _tagColors = catalog.ToDictionary(t => t.Name, t => t.Color);
        }
        catch (Exception)
        {
            // Leave whatever we had; an uncoloured chip is better than a broken pane.
        }
    }
}
