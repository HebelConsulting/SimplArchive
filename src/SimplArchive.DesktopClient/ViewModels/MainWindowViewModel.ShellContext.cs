using System;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The window's side of the tab seam: the <see cref="IShellContext"/> a tab view-model is handed, and the
/// headless hooks that straddle the two.
/// </summary>
/// <remarks>
/// Its own file rather than more lines in <c>MainWindowViewModel.cs</c>, which is the largest entry on the
/// 1000-line debt list (#466) — and listed in <c>OverLimitFileCeilingTests</c> alongside it, because a new
/// partial that nothing watches is precisely how cost moves rather than leaves (#517).
/// </remarks>
public sealed partial class MainWindowViewModel
{
    /// <summary>The Intray tab (#517). Owns its own state; reaches this window only through <see cref="IShellContext"/>.</summary>
    public IntrayTabViewModel Intray { get; }

    // --- IShellContext: the whole of what a tab may ask of this window ---------------------------------
    // Explicit implementations, so widening the tabs' seam does not silently widen this class's public surface.

    void IStatusReporter.Report(string status) => Status = status;

    void IShellContext.SaveLayout() => SaveLayout();

    // Deliberately still Intray-specific: ADR 0729's trigger for ActivateTab(WorkbenchTab) is a SECOND tab
    // needing it, and no tab view-model switches tabs — every SelectedTab assignment is this window's, a
    // view's or Program.cs's. An enum of fifteen members for one caller is the speculative generalisation the
    // code-style rule warns about, so the trigger stays unfired.
    void IShellContext.ActivateIntray() => SelectedTab = 1;


    Guid? IShellContext.CurrentUserId => _currentUserId;

    /// <summary>The checked-out set changed: reload the folder on screen and re-raise this window's counts.</summary>
    Task IShellContext.CheckoutsChangedAsync() => RefreshAfterCheckoutChangeAsync();

    // After a check-out/check-in/override changes lock state: reload the open folder's list (lock glyphs) and
    // the Check-out tab count.
    private async Task RefreshAfterCheckoutChangeAsync()
    {
        if (_currentFolderId is { } folderId && _archiveDocumentId is null)
        {
            var selectedId = SelectedItem?.Id;
            await LoadFolderContentsAsync(folderId);
            if (selectedId is { } id && Items.FirstOrDefault(n => n.Id == id) is { } fresh)
            {
                SelectedItem = fresh;
            }
        }

        OnPropertyChanged(nameof(CheckoutCount));
        OnPropertyChanged(nameof(HasCheckouts));
    }


    OcrLanguageCatalog? IShellContext.OcrLanguages => _ocrLanguages;

    // Created on first ask rather than at login, and shared with this class's own check-out stash: there is one
    // drop-filing helper per session, not one per caller.
    DropFiling? IShellContext.DropFiling => _api is { } api ? _dropFiling ??= new DropFiling(api) : null;

    /// <summary>
    /// A tab changed a document on the server. Whether the detail pane happens to be showing that document is
    /// this window's question, not the tab's — the tab does not know what is selected, and should not.
    /// </summary>
    async Task IShellContext.DocumentChangedOnServerAsync(Guid documentId)
    {
        // Filing posts a feed comment and adds a version. If that document is the one open on the Repositories
        // tab, refresh its detail so the comment + the new version's preview show without a manual reselect.
        if (_selectedDocumentId != documentId)
        {
            return;
        }

        await LoadCommentsAsync(DetailHref("chat"));
        await LoadPreviewAsync(DetailHref("versions"));
        await LoadSystemFieldsAsync(DetailHref("self"), DetailHref("versions"), DetailTitle);
    }

    // --- headless verification hooks, split at the boundary --------------------------------------------
    // Each keeps the half that is this window's state and delegates the Intray half; the callers' names are
    // unchanged, so Program.cs and the desktop tests are untouched by the extraction.

    internal void PopulateIntrayDemoForScreenshot()
    {
        IsLoggedIn = true;
        UserEmail = "demo@simplarchive.local";
        SelectedTab = 1;
        Intray.PopulateDemo();
        Preview.Reset("Preview renders here (PDF/image/text).");
    }

    internal Task<bool> IntrayDropSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        return Intray.DropSelfTestAsync();
    }

    internal Task<bool> IntraySendSelfTestAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        return Intray.SendSelfTestAsync();
    }

    /// <summary>
    /// Every preview surface in the window — the Repositories one plus each tab's own.
    /// </summary>
    /// <remarks>
    /// ONE list, read by both the login wiring and the logout teardown, because they were two lists and drifted:
    /// the Search tab's preview was in neither, so it silently showed nothing (its <c>RenderAsync</c> begins
    /// <c>if (Api is null) return;</c>), and Check-out's and Recycle bin's kept their API client after sign-out.
    /// A new tab that owns a preview belongs here, and <c>DesktopPreviewWiringTests</c> fails if it is missing.
    /// </remarks>
    internal IReadOnlyList<PreviewViewModel> PreviewSurfaces =>
        [Preview, Intray.Preview, Search.Preview, RecycleBin.Preview, Checkout.Preview];

    /// <summary>Hands every preview surface the session's API client, or takes it away at sign-out.</summary>
    private void SetPreviewApi(SimplArchiveApiClient? api)
    {
        foreach (var preview in PreviewSurfaces)
        {
            preview.Api = api;
        }
    }
}
