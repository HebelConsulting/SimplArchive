using System.Net.Http.Json;
using Microsoft.JSInterop;
using MudBlazor;
using SimplArchive.Localization;

namespace SimplArchive.Client.Services;

/// <summary>
/// Copies the caller's WebDAV mount address — optionally deep-linked to one folder inside it.
/// </summary>
/// <remarks>
/// <para>
/// A browser may not mount a drive, so where the desktop's button MOUNTS and OPENS the folder, the web client's
/// nearest honest equivalent is to hand over the address of that same folder (ADR 0560: a capability the browser
/// lacks is a divergence, not a gap). Both the Intray and Check-out tabs offer it, each for its own folder.
/// </para>
/// <para>
/// Its own service rather than a method on the workbench page: two tabs need it, and the shell is being
/// decomposed and may only shrink (ADR 0558) — putting it there is what pushed the page over its ceiling, which
/// is precisely what that guard exists to catch.
/// </para>
/// </remarks>
public sealed class WebDavMountLink
{
    private readonly HttpClient _http;
    private readonly ApiRoot _apiRoot;
    private readonly IJSRuntime _js;
    private readonly ISnackbar _snackbar;
    private readonly BrowseService _browse;

    public WebDavMountLink(HttpClient http, ApiRoot apiRoot, IJSRuntime js, ISnackbar snackbar, BrowseService browse)
    {
        _http = http;
        _apiRoot = apiRoot;
        _js = js;
        _snackbar = snackbar;
        _browse = browse;
    }

    /// <summary>Copies the address of a folder inside the caller's PERSONAL SPACE — "Intray", "Check-out".</summary>
    /// <remarks>
    /// The caller passes the leaf, and this resolves the space's own name (ADR 0671). Both tabs used to spell
    /// out "Personal/…" as a constant, which addressed a folder that does not exist — the link was copied
    /// happily and simply did not resolve. Resolved here rather than in each tab so there is one place that
    /// knows how that path is built, and one round trip that can be cached.
    /// </remarks>
    public async Task CopyPersonalFolderAsync(string leaf)
    {
        _personalSpaceName ??= (await _browse.EnsurePersonalRepositoryAsync())?.Name;
        await CopyAsync(SimplArchive.Presentation.WebDavPaths.InPersonalSpace(_personalSpaceName, leaf));
    }

    private string? _personalSpaceName;

    /// <param name="subFolder">The folder within the single mount, or empty for the whole tree.</param>
    public async Task CopyAsync(string subFolder)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<WebDavStatus>(await _apiRoot.RequireMeAsync("webdavPassword"));
            if (status is not { Enabled: true } || string.IsNullOrEmpty(status.Url))
            {
                _snackbar.Add(Strings.Get("StSetUpWebDavFirst"), Severity.Info);
                return;
            }

            // The single "SimplArchive" resource (ADR 0509) — mounting it lists the whole tree (Personal, with
            // Intray/Check-out, + the shared repositories). Appending the tab's folder is FOLLOWING the address
            // the server advertised, not composing one: the server owns the path, the caller adds only the
            // folder within it.
            var mountUrl = status.Url.TrimEnd('/');
            if (!string.IsNullOrEmpty(subFolder))
            {
                mountUrl += "/" + string.Join('/', subFolder.Split('/').Select(Uri.EscapeDataString));
            }

            await _js.InvokeVoidAsync("navigator.clipboard.writeText", mountUrl);
            _snackbar.Add(string.Format(Strings.Get("StCopiedWebDavUrl"), mountUrl), Severity.Success);
        }
        catch (Exception)
        {
            _snackbar.Add(Strings.Get("StErrCopyWebDavUrl"), Severity.Error);
        }
    }

    private sealed record WebDavStatus
    {
        public bool Enabled { get; set; }

        public string Url { get; set; } = string.Empty;
    }
}
