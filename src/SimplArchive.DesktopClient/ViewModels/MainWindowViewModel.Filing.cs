using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Everything that puts BYTES into the desktop workbench: files dropped onto the contents pane as new documents,
// files dropped onto a document row, and the filing decision a drop onto an existing document raises. Each ends
// the same way -- create a version, PUT the bytes straight to object storage, finalize against the address the
// create response advertised (ADR 0543; the Api never proxies the bytes).
//
// The web client's half of this subject is Home.Filing.razor.cs, and the two are named to match on purpose.
// ADR 0511 asks that a web/desktop pair be reviewed as a SINGLE surface, which is only possible if the two
// halves can be found -- and this one could not: it sat under a heading reading "Tag chip editor", which was a
// three-subject grab-bag (#941). A subject that is hard to locate on one side is exactly the condition under
// which the two clients drift apart without anyone noticing.
//
// A partial rather than a type of its own: these read and write the view model's own state -- the open folder,
// the tree, the status line -- so a separate type would take the view model as a parameter and be a partial
// wearing a constructor.
public sealed partial class MainWindowViewModel
{
    // Uploads files dropped onto the contents pane as new documents. Dropped onto a folder row, they go into
    // that folder (overrideFolderId); anywhere else, the currently-open folder. See ADR "Desktop drag-and-drop
    // upload" + ADR "List-pane drop filing".
    public async Task UploadDroppedFilesAsync(IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files, IReadOnlyDictionary<string, string>? targetFolderLinks = null)
    {
        // The drop target's own addresses, carried by the row it landed on (ADR 0555), else the open folder's.
        // Resolved ONCE for the whole drop — following a rel must not cost a request per file (ADR 0557).
        var folderLinks = targetFolderLinks ?? _currentFolderLinks;
        if (_api is null || folderLinks is null)
        {
            Status = Strings.Get("StSelectFolderDrop");
            return;
        }

        // A POST to the `children` address IS the create (ADR 0637) — the separate `create-child` rel that
        // question the drop target and the menu entry were gated on. It is also the backstop for the path
        // neither of those covers — a drop on the EMPTY list area falls back to the open folder, which with
        // `Personal` open is the first level that refuses it (#634).
        if (!folderLinks.TryGetValue("children", out var childrenHref))
        {
            ReportError(Strings.Get("StErrUploadNotHere"));
            return;
        }

        var uploaded = 0;
        var failed = 0;
        foreach (var file in files)
        {
            // Declared out here so the name-conflict recovery below can re-file the SAME bytes rather than
            // reading the file a second time.
            byte[] bytes = [];
            try
            {
                Status = string.Format(Strings.Get("StUploadingFile"), file.Name);
                await using (var stream = await file.OpenReadAsync())
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer);
                    bytes = buffer.ToArray();
                }

                // Duplicate detection (ADR "Duplicate document detection"): if an identical document already exists,
                // offer to reference it / file anyway / cancel before uploading a second copy.
                if (DuplicateUploadDialog is { } prompt)
                {
                    var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

                    // For an .eml the Message-ID joins the probe (#704) — byte-different copies of one
                    // message still meet in the dialog; .msg degrades to hash-only (ADR 0686).
                    var entryId = file.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)
                        ? SimplArchive.Presentation.MessageIdHeader.Extract(bytes)
                        : null;
                    var dups = await _api.Documents.FindDuplicatesAsync(hash, entryId);
                    if (dups.Count > 0)
                    {
                        var choice = await prompt(new DuplicatePromptRequest(file.Name, dups));
                        if (choice is null || choice.Action == "cancel")
                        {
                            continue;
                        }

                        if (choice.Action == "reference")
                        {
                            await _api.References.CreateReferenceAsync(folderLinks["references"], choice.TargetId);
                            uploaded++;
                            continue;
                        }
                        // "file" → fall through and upload a second copy.
                    }
                }

                await _api.Documents.UploadFileAsync(childrenHref, file.Name, bytes);
                uploaded++;
            }
            catch (Services.DocumentNameTakenException) when (NameConflictDialog is not null)
            {
                // The name is taken. Reporting that and dropping the file is what made a drag-and-drop appear to
                // do nothing, so ask what was meant instead (a new version, or a new name) and carry it out.
                var resolver = new Services.UploadConflictResolver(_api);
                if (await resolver.ResolveAsync(childrenHref, file.Name, bytes, NameConflictDialog, m => Status = m))
                {
                    uploaded++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Services.ApiActionException e)
            {
                ReportError(e.Message);
                failed++;
            }
            catch (Exception e)
            {
                ReportError(string.Format(Strings.Get("StErrUpload2"), file.Name, e.Message));
                failed++;
            }
        }

        if (_currentFolderId is { } openFolderId)
        {
            await LoadFolderContentsAsync(openFolderId);
        }

        ReportError(string.Format(Strings.Get("StUploadedN"), uploaded) + (failed > 0 ? string.Format(Strings.Get("StFailedN"), failed) : "") + ".");
    }

    // Dropping OS files onto a document row offers the intray-style filing dialog (ADR "List-pane drop filing"):
    // file as a new version of that document, or into its folder, with an optional feed comment. Builds the
    // picker VM (single-file → as-version available; multi-file → bulk, folder-only). The view shows the dialog
    // and calls FileDroppedFilesAsync with the result.
    public FolderPickerViewModel? CreateDropFilingPickerViewModel(NodeViewModel document, int fileCount)
    {
        if (_api is null || document.IsFolder)
        {
            return null;
        }

        var folderId = document.IsReference ? document.RealParentId ?? _currentFolderId : _currentFolderId;
        if (folderId is not { } fid)
        {
            return null;
        }

        var folderPath = string.Join(" / ", Breadcrumbs.Select(b => b.Name));
        var folderName = Breadcrumbs.LastOrDefault()?.Name ?? "";
        var context = new DocumentFilingContext(document.Id, document.Name, $"{folderPath} / {document.Name}", fid, folderName, folderPath,
            document.Links,
            // "Into its folder": a reference row's real home travels as its `go-to` address; a real row files
            // into the OPEN folder, whose links the navigation stored (ADR 0555).
            document.IsReference ? document.Links?.GetValueOrDefault("go-to") is { } goTo ? new Dictionary<string, string> { ["self"] = goTo } : null : _currentFolderLinks);
        return new FolderPickerViewModel(_api, context, bulk: fileCount > 1);
    }

    // Applies a list-pane drop-filing choice to the dropped files (ADR "List-pane drop filing"): file as a new
    // version of the target document, or as new documents in the chosen folder, each carrying the feed comment.
    public async Task FileDroppedFilesAsync(IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files, FilingResult result)
    {
        if (_api is null || files.Count == 0)
        {
            return;
        }

        var done = 0;
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                Status = string.Format(Strings.Get("StFilingFile"), file.Name);
                byte[] bytes;
                await using (var stream = await file.OpenReadAsync())
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer);
                    bytes = buffer.ToArray();
                }

                if (result.Mode == FilingMode.AsVersion)
                {
                    await _api.Documents.UploadNewVersionAsync(await TargetHrefAsync(result, "versions"), bytes, Path.GetExtension(file.Name), result.Comment);
                }
                else
                {
                    await _api.Documents.UploadFileAsync(await TargetHrefAsync(result, "children"), file.Name, bytes, result.Comment);
                }

                done++;
            }
            catch (Services.ApiActionException e)
            {
                ReportError(e.Message);
                failed++;
            }
            catch (Exception e)
            {
                ReportError(string.Format(Strings.Get("StErrFiling2"), file.Name, e.Message));
                failed++;
            }
        }

        // Reloading the folder rebuilds Items (clearing the selection), so capture the target first.
        var refreshDetailFor = result.Mode == FilingMode.AsVersion && SelectedItem?.Id == result.TargetId ? result.TargetId : (Guid?)null;
        if (_currentFolderId is { } folderId)
        {
            await LoadFolderContentsAsync(folderId);
        }

        // If we filed a new version of the currently-open document, refresh its detail (new version + comment).
        if (refreshDetailFor is { } targetId && Items.FirstOrDefault(n => n.Id == targetId) is { } node)
        {
            SelectedItem = node;
            await LoadDetailAsync(node);
        }

        Status = result.Mode == FilingMode.AsVersion
            ? $"Filed {done} new version(s)" + (failed > 0 ? $", {failed} failed" : "") + "."
            : $"Filed {done} file(s)" + (failed > 0 ? $", {failed} failed" : "") + ".";
    }

    // The filing target's address for a rel: from the links the picked row carried where it advertised the
    // rel, else resolved once through the row's document address (ADR 0559).
    private async Task<string> TargetHrefAsync(FilingResult result, string rel) =>
        result.TargetLinks?.GetValueOrDefault(rel)
        ?? await _api!.Documents.RelViaSelfAsync(
            result.TargetLinks?.GetValueOrDefault("self")
            ?? throw new InvalidOperationException($"The filing target advertised no address at all (ADR 0543)."),
            rel);
}
