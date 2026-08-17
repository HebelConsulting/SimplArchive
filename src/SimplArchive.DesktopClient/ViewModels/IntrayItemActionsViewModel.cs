using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// What a user can do TO a staged intray item: send it on, claim it into their own intray, delete it, and take
/// its pages apart or together (issue #487).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>MainWindowViewModel</c> rather than added to it. That file is over the 1000-line ceiling,
/// so it is a standing debt that may not grow — and these actions are one cohesive thing in their own right:
/// they all take an item, act on it through an address the LISTING advertised, and end by refreshing.
/// </para>
/// <para>
/// It holds no api client of its own. <see cref="Connect"/> hands it the same four seams the rest of the
/// workbench uses — the client, the refresh, the status line, and who "me" is — as functions rather than
/// values, because all four change when a session is replaced and a captured copy would go stale.
/// </para>
/// </remarks>
public sealed partial class IntrayItemActionsViewModel : ObservableObject
{
    private Func<SimplArchiveApiClient?>? _api;
    private Func<Task>? _refresh;
    private Action<string>? _setStatus;
    private Func<Guid?>? _me;

    public void Connect(
        Func<SimplArchiveApiClient?> api,
        Func<Task> refresh,
        Action<string> setStatus,
        Func<Guid?> currentUserId)
    {
        _api = api;
        _refresh = refresh;
        _setStatus = setStatus;
        _me = currentUserId;
    }

    /// <summary>
    /// The intray collection's own `join` address, captured where the listing was read (ADR 0557).
    /// </summary>
    /// <remarks>
    /// Null until an intray has been listed, and null again if the server stops offering it — which is the
    /// client's cue to keep the Join affordance hidden rather than to compose the URL itself (ADR 0543).
    /// </remarks>
    [ObservableProperty] private string? _joinHref;

    /// <summary>The printable Patch 3 separator sheet's address, captured from the same listing (#492).</summary>
    [ObservableProperty] private string? _patchCodeSheetHref;

    /// <summary>
    /// What the SELECTED item's pages can do, or null when the server offers nothing — a format with no page
    /// sequence, a one-page file, or no selection at all.
    /// </summary>
    /// <remarks>
    /// Refreshed on every selection change. A rel that has not arrived means "not available to you, here, now"
    /// (ADR 0543), which during a load is exactly true — so this is CLEARED before the new item is asked about,
    /// rather than left describing the previous selection while the request is in flight (ADR 0559).
    /// </remarks>
    [ObservableProperty] private IntrayApi.PagesInfo? _pages;

    /// <summary>How many intray rows are selected — Join needs at least two.</summary>
    [ObservableProperty] private int _selectedCount;

    public bool CanSplit => Pages?.CanSplit == true;

    public bool CanSort => Pages?.CanSort == true;

    public bool CanJoin => SelectedCount > 1 && JoinHref is not null;

    public bool CanDeskew => Pages?.CanDeskew == true;

    public bool CanCutAtPatchCodes => Pages?.CanCutAtPatchCodes == true;

    /// <summary>
    /// Whether crooked scans are straightened automatically for this user (#491) — the ribbon toggle's state,
    /// held down while on.
    /// </summary>
    /// <remarks>
    /// A server-side preference rather than a local setting, because the Worker's sweep reads it for items
    /// arriving over WebDAV. Setting it writes through; a failure puts it back rather than leaving the button
    /// showing a state the server does not hold.
    /// </remarks>
    [ObservableProperty] private bool _deskewAutomatically = true;

    /// <summary>
    /// Turn a page that arrived 90 or 180 degrees round the right way up, as it arrives (#492).
    /// </summary>
    /// <remarks>
    /// Its own toggle beside straightening, not the same one: rotation on a PDF is only the page's /Rotate
    /// attribute and so is lossless, which is why it may run on PDFs at all — while deskew has to re-render
    /// the page and therefore declines them. One switch could not honestly describe both.
    /// </remarks>
    [ObservableProperty] private bool _rotateAutomatically = true;

    /// <summary>
    /// Whether an arriving batch scan is cut into one item per document at its separator sheets (#492) — the
    /// second ribbon toggle, and a sibling of the one above in every respect.
    /// </summary>
    [ObservableProperty] private bool _cutAtPatchCodesAutomatically = true;

    partial void OnPagesChanged(IntrayApi.PagesInfo? value)
    {
        OnPropertyChanged(nameof(CanSplit));
        OnPropertyChanged(nameof(CanSort));
        OnPropertyChanged(nameof(CanDeskew));
        OnPropertyChanged(nameof(CanCutAtPatchCodes));
    }

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(CanJoin));

    partial void OnJoinHrefChanged(string? value) => OnPropertyChanged(nameof(CanJoin));

    /// <summary>
    /// Asks what the newly selected item's pages can do. One request, and only for a row whose name says it
    /// might have pages — the api client returns null without a call when the row advertised no `pages` rel.
    /// </summary>
    public async Task LoadPagesAsync(IntrayItemViewModel? item)
    {
        Pages = null;
        if (item is not null)
        {
            Pages = await GetPagesAsync(item);
        }
    }

    public async Task DeleteAsync(IntrayItemViewModel item)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        await api.Intray.DeleteIntrayItemAsync(item.Item!);
        await RefreshAsync();
    }

    // The "Send to…" destinations for the dialog (ADR 0532): the caller's groups followed by the other users.
    public async Task<IReadOnlyList<IntrayApi.IntrayTargetInfo>> GetSendTargetsAsync()
    {
        if (_api?.Invoke() is not { } api)
        {
            return [];
        }

        var groups = await api.Intray.GetIntrayGroupsAsync();
        var users = await api.Intray.GetIntrayUsersAsync();
        return groups.Concat(users).ToList();
    }

    // Sends an own item into a chosen group or user's intray (ADR 0532), then refreshes.
    public async Task SendAsync(IntrayItemViewModel item, IntrayApi.IntrayTargetInfo target)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await api.Intray.MoveIntrayItemAsync(item.MoveUrl, target.IsGroup ? target.Id : null, target.IsGroup ? null : target.Id);
            Status(string.Format(Strings.Get("StMoved"), item.Name));
        });
    }

    // Claims a non-own (group / other-user) item into my own intray (ADR 0532), then refreshes.
    public async Task ClaimToMineAsync(IntrayItemViewModel item)
    {
        if (_api?.Invoke() is not { } api || _me?.Invoke() is not { } me)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await api.Intray.MoveIntrayItemAsync(item.MoveUrl, null, me);
            Status(string.Format(Strings.Get("StMoved"), item.Name));
        });
    }

    // ---- Page operations (#487, ADR 0575) --------------------------------------------------------------

    /// <summary>
    /// The item's pages and which page operations it can take, or null when the server offers none.
    /// </summary>
    public async Task<IntrayApi.PagesInfo?> GetPagesAsync(IntrayItemViewModel item) =>
        _api?.Invoke() is { } api && item.Item is { } info ? await api.Intray.GetAsync(info) : null;

    /// <summary>Splits the item into one intray item per page, keeping the source.</summary>
    public async Task SplitAsync(IntrayItemViewModel item, string splitHref)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var written = await api.Intray.SplitAsync(splitHref);
            Status(string.Format(Strings.Get("StIntraySplit"), item.Name, written.Count));
        });
    }

    /// <summary>Rewrites the item's pages in the given order (1-based, each page exactly once).</summary>
    public async Task SortAsync(IntrayItemViewModel item, string sortHref, IReadOnlyList<int> pageOrder, IReadOnlyDictionary<int, int>? rotations = null)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await api.Intray.SortAsync(sortHref, pageOrder, rotations);
            Status(string.Format(Strings.Get("StIntraySorted"), item.Name));
        });
    }

    /// <summary>Straightens one item on demand — the deliberate counterpart to the automatic path.</summary>
    public async Task DeskewAsync(IntrayItemViewModel item, string deskewHref)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var straightened = await api.Intray.DeskewAsync(deskewHref);
            Status(string.Format(
                Strings.Get(straightened.Length > 0 ? "StIntrayDeskewed" : "StIntrayDeskewNothing"),
                straightened.Length > 0 ? straightened : item.Name));
        });
    }

    /// <summary>Cuts one batch scan at its separator sheets, on demand (#492).</summary>
    public async Task CutAtPatchCodesAsync(IntrayItemViewModel item, string patchCodesHref)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var parts = await api.Intray.CutAtPatchCodesAsync(patchCodesHref);
            Status(string.Format(Strings.Get("StIntrayCutAtPatchCodes"), item.Name, parts.Count));
        });
    }

    /// <summary>
    /// Fetches the printable separator sheet and opens it in whatever prints PDFs on this machine (#492).
    /// </summary>
    /// <remarks>
    /// Opening it rather than saving it somewhere: the sheet's whole purpose is to come out of a printer, and
    /// a file the user then has to find is a step between them and the only thing they wanted.
    /// </remarks>
    public async Task OpenPatchCodeSheetAsync()
    {
        if (_api?.Invoke() is not { } api || PatchCodeSheetHref is not { } href)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var sheet = await api.Intray.GetBytesAsync(href);
            await NativeFileOpener.OpenBytesAsync(sheet, "SimplArchive-Patch3-Separator.pdf");
        });
    }

    /// <summary>Reads both ribbon preferences at sign-in, in one request.</summary>
    public async Task LoadIngestPreferencesAsync()
    {
        if (_api?.Invoke() is { } api)
        {
            var preferences = await api.Intray.GetPreferencesAsync();
            DeskewAutomatically = preferences.Deskew;
            RotateAutomatically = preferences.Rotate;
            CutAtPatchCodesAutomatically = preferences.CutAtPatchCodes;
        }
    }

    /// <summary>Writes the straighten toggle through, putting the button back if the server refuses.</summary>
    public Task SetDeskewAutomaticallyAsync(bool enabled) =>
        SetPreferenceAsync(
            enabled,
            DeskewAutomatically,
            value => DeskewAutomatically = value,
            (api, on) => api.Intray.SetDeskewPreferenceAsync(on));

    /// <summary>Writes the rotate toggle through, on the same terms.</summary>
    public Task SetRotateAutomaticallyAsync(bool enabled) =>
        SetPreferenceAsync(
            enabled,
            RotateAutomatically,
            value => RotateAutomatically = value,
            (api, on) => api.Intray.SetRotatePreferenceAsync(on));

    /// <summary>Writes the cut-at-separators toggle through, on the same terms.</summary>
    public Task SetCutAtPatchCodesAutomaticallyAsync(bool enabled) =>
        SetPreferenceAsync(
            enabled,
            CutAtPatchCodesAutomatically,
            value => CutAtPatchCodesAutomatically = value,
            (api, on) => api.Intray.SetPatchCodePreferenceAsync(on));

    // Optimistic, then reverted on refusal — a toggle that stays down after the server said no is a button
    // claiming a state nobody holds. Which property and which call differ; the shape does not, so it is stated
    // once and the difference arrives as two lambdas.
    private async Task SetPreferenceAsync(
        bool enabled,
        bool previous,
        Action<bool> set,
        Func<SimplArchiveApiClient, bool, Task> write)
    {
        if (_api?.Invoke() is not { } api)
        {
            return;
        }

        set(enabled);
        try
        {
            await write(api, enabled);
        }
        catch (ApiActionException e)
        {
            set(previous);
            Status(e.Message);
        }
    }

    /// <summary>Joins the named items into one, in the order given, keeping the sources.</summary>
    public async Task JoinAsync(IReadOnlyList<string> names, string? targetName)
    {
        if (_api?.Invoke() is not { } api || JoinHref is not { } joinHref)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var joined = await api.Intray.JoinAsync(joinHref, names, targetName);
            Status(string.Format(Strings.Get("StIntrayJoined"), names.Count, joined));
        });
    }

    // Every action ends the same way: the server's own words on refusal (its RFC 7807 `detail` says WHICH page
    // was listed twice), and a refresh either way — a failed operation can still have been preceded by one that
    // succeeded, so the list must not be left describing a state that no longer exists.
    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiActionException e)
        {
            Status(e.Message);
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refresh is { } refresh)
        {
            await refresh();
        }
    }

    private void Status(string message) => _setStatus?.Invoke(message);
}
