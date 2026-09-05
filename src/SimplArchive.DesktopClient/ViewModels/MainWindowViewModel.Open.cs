using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// What happens when a row is OPENED: the native open (⌘/Ctrl+O, the ribbon button and the row's own action,
// ADR 0568), and the one case where opening does not hand the file to the OS at all -- a .zip, which is
// browsed in place, its entries read on demand with nothing unpacked (ADR "Zip file browsing").
//
// One subject rather than two: stepping into an archive is what opening a .zip DOES, so the branch belongs
// beside the open it is a branch of. Exiting restores the list, which is why the breadcrumb and current folder
// are deliberately left untouched while browsing.
//
// It came out of a heading reading "Folder detail pane: the open folder's persisted contents sort order",
// which covered SIX subjects across 322 lines -- sort order, the detail glyph, the breadcrumb, opening, the
// archive browser, and folder creation (#941). The four that remain are still to be dealt out.
//
// A partial rather than a type of its own: opening reads the current selection and replaces the contents list,
// both of which are this view model's own state.
public sealed partial class MainWindowViewModel
{
    // "Open (⌘O)" for the affordances that are a plain button rather than a menu entry — the ribbon's Open and
    // the Intray row's Open — since only a MenuItem can carry an InputGesture. Composed from the localized label
    // plus the platform chord, so it needs no resource of its own.
    // Trailing after a dash, not in brackets: the ribbon's own label is already a parenthesised sentence, and a
    // second bracket inside a tooltip reads as a nested aside rather than as a shortcut.
    private static string WithOpenChord(string labelKey) => $"{Strings.Get(labelKey)} — {Services.Shortcuts.Open}";

    public static string OpenTip => WithOpenChord("MwOpen");
    public static string RibbonOpenTip => WithOpenChord("RibbonOpen");

    // ⌘/Ctrl+O on the current tab's selected row (#482, ADR "One shortcut for opening a document"). Opening is
    // the most frequent action in the product and needed a right-click and a menu pick every time.
    //
    // Deliberately only the two tabs whose Open means **open in the native application**. Search and Tasks have
    // an "Open" too, but theirs REVEALS the document in Repositories — a different action wearing the same word,
    // and one chord that means two things is a chord nobody trusts. Check-out and the Recycle bin have no Open
    // at all, and ADR 0554 says an action that cannot succeed is not advertised.
    //
    // Addressed from the SELECTION, never from a pane's loaded state (ADR 0559): both commands read the selected
    // row, which is set synchronously on click, so a shortcut pressed mid-load still acts on what is selected.
    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        switch (SelectedTab)
        {
            case 0 when OpenCommand.CanExecute(null):
                await OpenCommand.ExecuteAsync(null);
                break;
            case 1 when Intray.SelectedServerItem is not null:
                await Intray.OpenServerItemCommand.ExecuteAsync(null);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenAsync()
    {
        if (SelectedItem is not { } node || _api is null)
        {
            return;
        }

        if (node.IsArchiveBack)
        {
            await ExitArchiveAsync();
            return;
        }

        if (node.IsArchiveEntry)
        {
            // A zip entry has no presigned URL — fetch its bytes through the authenticated Api, write them to
            // the temp folder, and hand off to the OS app (task #7, ADR "Zip file browsing").
            try
            {
                var bytes = await DownloadArchiveEntryAsync(node);
                if (bytes is null)
                {
                    ReportError(string.Format(Strings.Get("StErrReadArchive"), node.Name));
                    return;
                }

                await NativeFileOpener.OpenBytesAsync(bytes, Path.GetFileName(node.Name.Replace('\\', '/')));
                Status = string.Format(Strings.Get("StOpenedNative"), node.Name);
            }
            catch (Exception e)
            {
                ReportError(string.Format(Strings.Get("StErrOpen2"), node.Name, e.Message));
            }

            return;
        }

        if (node.IsFolder || node.HasChildren)
        {
            // Drill into a folder, or a document that has child documents (an email with filed attachments,
            // ADR "Email attachments as child documents") — append it to the breadcrumb path and list its
            // contents.
            Breadcrumbs.Add(new BreadcrumbViewModel { Name = node.Name, FolderId = node.Id, ShowSeparator = Breadcrumbs.Count > 0, Links = node.Links });
            await LoadFolderContentsAsync(node.Id, node.Links);
            return;
        }

        try
        {
            // Fetch the preview to resolve the version's file extension (Document.Name is a bare stem now —
            // ADR "Extension off Document.Name"), needed both to spot a .zip and to name the opened temp file.
            var preview = await _api.Documents.GetPreviewAsync(node.Href("versions"));

            if (node.HasVersions && string.Equals(preview.FileExtension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Browse the .zip's entries virtually — nothing unpacked (ADR "Zip file browsing").
                await EnterArchiveAsync(node);
                return;
            }

            if (preview.DownloadUrl is null)
            {
                Status = string.Format(Strings.Get("StNoDownloadable"), node.Name);
                return;
            }

            // The temp file needs the extension so the OS picks the right application.
            await NativeFileOpener.OpenAsync(preview.DownloadUrl, WithExtension(node.Name, preview.FileExtension));
            Status = string.Format(Strings.Get("StOpenedNative"), node.Name);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrOpen2"), node.Name, e.Message));
        }
    }

    // Browses a .zip's entries virtually — read on demand, nothing unpacked (ADR "Zip file browsing"). The
    // list is replaced with a "back" row + one row per entry; the breadcrumb/current folder are left as-is so
    // exiting returns to them.
    private async Task EnterArchiveAsync(NodeViewModel zip)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            // `archive-entries` is CONDITIONAL on the resource — its presence is the server answering "can I
            // browse inside this?" — so it is resolved through the row's document address (ADR 0559).
            var entries = await _api.Documents.GetArchiveEntriesAsync(await _api.Documents.RelViaSelfAsync(zip.DocumentSelfHref, "archive-entries"));
            _archiveDocumentId = zip.Id;
            CanCreateFolder = false;
            CanExport = false;

            Items.Clear();
            Items.Add(new NodeViewModel { Id = Guid.Empty, Name = $"⬆ {zip.Name}", HasChildren = false, HasVersions = false, IsArchiveBack = true });
            foreach (var entry in entries)
            {
                Items.Add(new NodeViewModel
                {
                    Id = Guid.Empty,
                    Name = entry.Path,
                    HasChildren = false,
                    HasVersions = true,
                    IsArchiveEntry = true,
                    ArchiveEntryPath = entry.Path,
                    ArchiveEntryDownloadHref = entry.DownloadHref,
                });
            }

            Status = string.Format(Strings.Get("StArchiveEntries"), entries.Count);
        }
        catch (Exception ex)
        {
            ReportError(string.Format(Strings.Get("StErrRead2"), zip.Name, ex.Message));
        }
    }

    private async Task ExitArchiveAsync()
    {
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId); // clears _archiveDocumentId and re-lists the folder
        }
        else
        {
            _archiveDocumentId = null;
        }
    }

    // Downloads one archive entry's bytes for the Save-as flow (the view picks the destination).
    public async Task<byte[]?> DownloadArchiveEntryAsync(NodeViewModel entry)
    {
        if (_api is null || entry.ArchiveEntryDownloadHref is not { } href)
        {
            return null;
        }

        return await _api.DownloadArchiveEntryAsync(href);
    }

    // Resolves the latest confirmed version's download URL for a document (the view then shows a Save-as
    // dialog and writes the bytes to the chosen location). Null if there's no downloadable version.
    // The presigned download URL plus a suggested filename = Document.Name (the stem) + the version's file
    // extension (ADR "Extension off Document.Name"), so Save-as writes e.g. "scan.tif", not the extension-less
    // "scan".
    public async Task<(string? Url, string FileName)> GetDownloadInfoAsync(NodeViewModel node)
    {
        if (_api is null || node.IsFolder)
        {
            return (null, node.Name);
        }

        var preview = await _api.Documents.GetPreviewAsync(node.Href("versions"));
        return (preview.DownloadUrl, WithExtension(node.Name, preview.FileExtension));
    }

    // Reconstructs a filename from Document.Name (a bare stem, ADR "Extension off Document.Name") + the
    // version's extension — but only appends when the name doesn't already carry it, so pre-extension-change
    // data (whose Name still includes the extension) doesn't get a doubled ".zip.zip".
    internal static string WithExtension(string name, string extension) =>
        string.IsNullOrEmpty(extension) || name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + extension;
}
