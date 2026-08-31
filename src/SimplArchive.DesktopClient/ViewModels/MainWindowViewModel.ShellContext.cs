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

    void IShellContext.Report(string status) => Status = status;

    void IShellContext.SaveLayout() => SaveLayout();

    void IShellContext.ActivateIntray() => SelectedTab = 1;

    Guid? IShellContext.CurrentUserId => _currentUserId;

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
}
