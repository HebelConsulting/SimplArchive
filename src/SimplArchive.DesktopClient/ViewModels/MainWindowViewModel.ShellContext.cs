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

    // "Go to" from the Calendar and Contacts tabs (#1122). The same two reveal paths the search hit and the
    // legal-hold item already take — a document filed at a repository root IS a top-level tree node, so it has
    // no parent to expand and takes the folder path instead.
    async Task IShellContext.RevealDocumentAsync(Guid documentId, string documentHref, string? parentHref)
    {
        SelectedTab = 0;   // Repositories — the bare index is this window's own convention (see ActivateIntray)
        if (parentHref is { } parent)
        {
            await RevealDocumentInTreeAsync(documentId, documentHref, parent);
        }
        else
        {
            await RevealFolderInTreeAsync(documentHref);
        }
    }


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

    /// <summary>
    /// Throws away everything the card decrypted — called when the card leaves the reader (#1353, ADR 0832).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user stays signed in (owner, 2026-09-26). What goes is the CONTENT that the card decrypted: a preview
    /// surface holding enveloped bytes is reset, so no page rendered from a decrypted document is still on screen
    /// or still held as a bitmap. Metadata, the tree, listings and search results are unaffected — none of them
    /// was enveloped.
    /// </para>
    /// <para>
    /// <b>And neither is a preview that was never enveloped</b> (#1450). This said "every preview surface is
    /// reset" and meant it, so on a tenant with no encryption mode — the public demo — taking a card out of the
    /// reader blanked an ordinary plaintext document. The sentence after it already gave the right rule
    /// ("unaffected — none of them was enveloped"); only the loop disagreed.
    /// </para>
    /// <para>
    /// Uses the one <see cref="PreviewSurfaces"/> list rather than naming panes, for the reason recorded there:
    /// the two lists that preceded it drifted, and a tab missing from one silently kept working with stale
    /// state. Here that would mean a decrypted page left on screen after the card was pulled — the exact thing
    /// this method exists to prevent — which is why it must never become its own list.
    /// </para>
    /// </remarks>
    public void DiscardDecryptedContent(string placeholder)
    {
        // ONLY what the card actually decrypted (#1450). This reset every surface unconditionally, so removing a
        // card blanked a plaintext document on the public demo tenant — which has no encryption mode at all, and
        // where nothing had ever been enveloped. The wiring's own comment already said this was the intent
        // ("metadata browsing is unaffected because none of it was ever enveloped"); the code did not check it.
        //
        // Asked of each SURFACE rather than of the session, because the two differ in the case that matters: a
        // reader on a strict tenant who opens an enveloped document and then a plaintext one would otherwise
        // have the plaintext one blanked too — the same confusion, rarer, and no less wrong.
        _closedByCardRemoval.Clear();

        foreach (var preview in PreviewSurfaces.Where(p => p.ContentWasEnveloped))
        {
            // Remembered BEFORE the reset, so the card coming back can put it on screen again (#1453).
            if (preview.LastRendered is { } rendered)
            {
                _closedByCardRemoval.Add((preview, rendered));
            }

            preview.Reset(placeholder);
        }
    }

    private readonly List<(PreviewViewModel Surface, Services.Preview Rendered)> _closedByCardRemoval = [];

    /// <summary>Test seam: how many surfaces are waiting for the card to come back.</summary>
    internal int PendingCardRestoreCount => _closedByCardRemoval.Count;

    /// <summary>
    /// Puts back what the card's removal closed, when the card returns (#1453).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The placeholder left by the discard says "Put it back to read again", and it was not true: the document
    /// stayed closed until the reader selected a DIFFERENT one, which re-prompted for the PIN. Promising an
    /// action and then ignoring it is worse than not promising it.
    /// </para>
    /// <para>
    /// <b>Only a surface that has rendered nothing since</b> — compared by identity against what it was showing
    /// when it was closed. A reader who picked another document while the card was out has a current document,
    /// and replacing it with the previous one would be its own defect. Re-rendering asks for the PIN again,
    /// which is the point: the key left the machine and has to be presented again.
    /// </para>
    /// </remarks>
    public async Task RestoreContentClosedByCardRemovalAsync()
    {
        var pending = _closedByCardRemoval.ToList();
        _closedByCardRemoval.Clear();

        foreach (var (surface, rendered) in pending)
        {
            if (ReferenceEquals(surface.LastRendered, rendered))
            {
                await surface.RenderAsync(rendered);
            }
        }
    }

    /// <summary>
    /// Ends the session: revokes its tokens on the SERVER, then drops the API client here and from every preview.
    /// </summary>
    /// <remarks>
    /// The revocation (#1578, ADR 0895) is best-effort and runs in the background, so an offline sign-out is as
    /// immediate as an online one. Its own method — in this file, not in <c>Logout</c> — because the window's main
    /// file is on the over-limit debt list (#466) and may only shrink.
    /// </remarks>
    private void EndSession()
    {
        _ = _api?.Core.RevokeSessionAsync();
        _api = null;
        SetPreviewApi(null);
    }

    /// <summary>Hands every preview surface the session's API client, or takes it away at sign-out.</summary>
    private void SetPreviewApi(SimplArchiveApiClient? api)
    {
        foreach (var preview in PreviewSurfaces)
        {
            preview.Api = api;
        }
    }
}
