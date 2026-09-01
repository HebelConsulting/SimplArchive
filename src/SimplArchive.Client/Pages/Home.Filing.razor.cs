using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using SimplArchive.Client.Dialogs;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;
using SimplArchive.Client.Services;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// Everything that puts BYTES into the workbench: an ordinary drop onto a folder, a new version dropped onto a
// document row, an edited working copy coming back to Check-out, and the Intray's two paths (a drop, and a
// repository document used as a template). One subject in five parts, because the browser hands them all to the
// same place — dropUpload.js calls back into these [JSInvokable] members, and every one of them ends by creating
// a version, PUTting the bytes straight to object storage, and finalizing against the address the create
// response advertised (ADR 0543 — the Api never proxies the bytes).
//
// A partial of Home rather than a component, by ADR 0733's test: these have no markup of their own to bring.
// The drop surface is the list pane and the tree, which stay in the shell; what would come with a child is a
// bag of callbacks — the selection, the tree reload, the refresh, the snackbar — which is the shape that rule
// exists to refuse.
//
// The five headings below arrived under one that said "Upload orchestration" and then stopped describing what
// followed it, which is how 407 lines came to sit in the shell unremarked: inserting a member above a comment
// moves neither, so a banner ends up naming whatever was written first rather than what is there now.
public partial class Home
{
    // ---- Upload orchestration (called from dropUpload.js) --------------------------------------------

    // Comment: the filing note the user typed in the name-conflict dialog, carried through so FinalizeUploadAsync
    // can set it as the version comment (ADR 0528). Null for an ordinary drop, which asks for nothing.
    // FinalizeHref is the created version's own advertised address — where the finalize PUT goes. It rides
    // through JS untouched (dropUpload.js hands the whole target back), so the finalize step follows the rel
    // the create response stated rather than rebuilding the path from two ids (ADR 0543, #416).
    public record UploadTarget(string DocumentId, string VersionId, string UploadUrl, string? Comment = null, string? FinalizeHref = null);

    [JSInvokable]
    public Task OnUploadsStartingAsync(int count)
    {
        _uploading = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    // Called from dropUpload.js before each upload with the file's SHA-256 (ADR "Duplicate document detection").
    // Checks for an identical existing document; if found, shows the reference/file-anyway/cancel modal. Returns
    // "file" (upload normally), "referenced" (a shortcut was created instead — skip), or "cancel" (skip).
    [JSInvokable]
    public async Task<string> PrepareUploadAsync(string folderId, string hash, string fileName, string? headerText = null)
    {
        try
        {
            // For an .eml, dropUpload.js hands over the file's first bytes and the Message-ID joins the probe
            // (#704) — two recipients' copies of one message are never byte-identical, so the hash alone
            // cannot catch the one class where duplicates are the everyday case. Extracted HERE, in the
            // shared MessageIdHeader, so the two clients cannot parse the same header differently.
            var entryId = SimplArchive.Presentation.MessageIdHeader.Extract(headerText);

            // A query on an advertised href is following it (ADR 0557) — the root owns the path, the caller
            // owns the filter.
            var resp = await Http.GetFromJsonAsync<DuplicatesResponse>(
                $"{await ApiRoot.RequireAsync("duplicates")}?hash={hash}{(entryId is null ? string.Empty : $"&entryId={Uri.EscapeDataString(entryId)}")}");
            var dups = resp?.Duplicates ?? [];
            if (dups.Count == 0)
            {
                return "file";
            }

            var parameters = new DialogParameters
            {
                ["FileName"] = fileName,
                ["Duplicates"] = dups.Select(d => (d.Id, d.Name, d.Path)).ToList(),
            };
            var result = await (await DialogService.ShowAsync<Dialogs.DuplicateUploadDialog>(Strings.Get("DupDlgTitle"), parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true })).Result;
            if (result is not { Canceled: false } || result.Data is not Dialogs.DuplicateUploadDialog.DuplicateUploadChoice choice)
            {
                return "cancel";
            }

            if (choice.Action == "reference" && Guid.TryParse(folderId, out var folderGuid))
            {
                // The drop target arrives from JS as a bare id, so its references collection is reached by the
                // sanctioned fetch-then-follow (ADR 0543).
                var refResp = await Http.PostAsJsonAsync(await Browse.FetchRelAsync(folderGuid, "references"), new { targetId = choice.TargetId });
                if (refResp.IsSuccessStatusCode)
                {
                    Snackbar.Add(string.Format(Strings.Get("StReferencedInstead"), fileName), Severity.Success);
                }
                else if (refResp.StatusCode == HttpStatusCode.Conflict)
                {
                    Snackbar.Add(Strings.Get("StReferenceExists"), Severity.Warning);
                }
                else
                {
                    Snackbar.Add(Strings.Get("StErrCreateReference"), Severity.Error);
                }

                return "referenced";
            }

            return choice.Action == "file" ? "file" : "cancel";
        }
        catch (Exception)
        {
            // If the check fails, don't block the upload — fall through to the normal path.
            return "file";
        }
    }

    private record DuplicatesResponse { public List<DuplicateDto> Duplicates { get; set; } = []; }
    private record DuplicateDto { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; public string Path { get; set; } = string.Empty; }

    [JSInvokable]
    public async Task<UploadTarget?> CreateUploadTargetAsync(string folderId, string fileName)
    {
        try
        {
            // Document.Name is the bare stem; the extension goes on the version's object key via fileExtension
            // (ADR "Extension off Document.Name, derived from the object key").
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var extension = System.IO.Path.GetExtension(fileName);

            // JS hands over the drop target as a bare id, once per FILE — resolved via the sanctioned fetch
            // and memoised (ADR 0557: one read, many follows).
            var childrenHref = await MemoisedRelAsync(_childrenHrefByFolder, Guid.Parse(folderId), "children");
            var create = await Http.PostAsJsonAsync(childrenHref, new { name = stem });
            if (create.StatusCode == HttpStatusCode.Conflict)
            {
                // Taken: ask what was meant rather than warning and dropping the file, which made a drag-and-drop
                // look like it had done nothing (UploadConflictResolver owns the decision — ADR 0558).
                return await UploadConflicts.ResolveAsync(childrenHref, fileName, stem, extension) is { } r
                    ? new UploadTarget(r.DocumentId.ToString(), r.VersionId.ToString(), r.UploadUrl, r.Comment, r.FinalizeHref)
                    : null;
            }
            if (create.StatusCode == HttpStatusCode.Forbidden)
            {
                Snackbar.Add(string.Format(Strings.Get("StUploadNoPermission"), fileName), Severity.Warning);
                return null;
            }
            create.EnsureSuccessStatusCode();
            var created = await create.Content.ReadFromJsonAsync<DocumentSummary>();

            // The create response IS the new document, and it advertises its versions collection; the version
            // it creates advertises its own address, which is where the finalize PUT goes (ADR 0543, #416).
            var versionsHref = Links.Href(created!.Links, "versions")
                ?? throw new InvalidOperationException("The created document advertised no 'versions' rel (ADR 0543).");
            var versionResponse = await Http.PostAsJsonAsync(versionsHref, new { fileExtension = extension });
            versionResponse.EnsureSuccessStatusCode();
            var version = await versionResponse.Content.ReadFromJsonAsync<CreateVersionResponse>();

            return new UploadTarget(created.Id.ToString(), version!.Id.ToString(), version.UploadUrl, FinalizeHref: Links.Href(version.Links, "self"));
        }
        catch (Exception)
        {
            Snackbar.Add(string.Format(Strings.Get("StUploadNotStarted"), fileName), Severity.Error);
            return null;
        }
    }

    [JSInvokable]
    public async Task FinalizeUploadAsync(string finalizeHref, string fileName, string? comment)
    {
        try
        {
            // The filing comment is the version's "why this revision" note (ADR 0528) — set on the version at
            // finalize (the drop-upload created it first), not posted to the chat feed as it used to be. The
            // address is the one the create response advertised, carried through JS on the upload target.
            var versionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            (await Http.PutAsJsonAsync(finalizeHref, new { comment = versionComment })).EnsureSuccessStatusCode();

            // The server assigns the mask at finalize (eMail for .eml/.msg, else Basic Entry) — see ADR
            // "Email auto-classification"; the client no longer classifies.

            Snackbar.Add(string.Format(Strings.Get("StUploaded"), fileName), Severity.Success);
        }
        catch (Exception)
        {
            Snackbar.Add(string.Format(Strings.Get("StUploadNotFinalized"), fileName), Severity.Error);
        }
    }

    // ---- Personal ▸ Intray: a repository document as a TEMPLATE (#467) ----------------------------------
    //
    // Dragging a document onto Intray copies it in WITH its mask and index values, so new work can start from an
    // existing document without committing to a new document or version until it is filed. The copy happens
    // server-side (one request, no bytes through the browser, and no half-copied item if it fails).
    [JSInvokable]
    public async Task CopyDocumentToIntrayAsync(string documentId)
    {
        if (!Guid.TryParse(documentId, out var id))
        {
            return;
        }

        try
        {
            var intray = await Http.GetFromJsonAsync<IntrayLinksResponse>(await ApiRoot.RequireAsync("intray"));
            var href = Links.Href(intray?.Links, "from-document")
                ?? throw new InvalidOperationException("The intray advertised no 'from-document' rel (ADR 0543).");

            var response = await Http.PostAsJsonAsync(href, new { documentId = id });
            if (!await Actions.HandleMutationAsync(response,
                    Strings.Get("StTemplateCopied"), Strings.Get("StTemplateFailed"), Strings.Get("StTemplateNameTaken")))
            {
                return;
            }

            await SetTab(Tab.Intray);
            StateHasChanged();
        }
        catch (Exception)
        {
            Snackbar.Add(Strings.Get("StTemplateFailed"), Severity.Error);
        }
    }

    private sealed record IntrayLinksResponse { public List<LinkResponse> Links { get; set; } = []; }

    // ---- Personal ▸ Check-out drop: an edited working copy comes back (#467) ----------------------------
    //
    // The round trip is download → edit offline → drag back, so the FILENAME is what says which document the
    // working copy belongs to; the launcher node carries none. A file naming nothing checked out is refused
    // with that reason rather than ignored — "nothing happened" is how a feature gets reported as broken.

    [JSInvokable]
    public async Task<string?> CreateStashTargetForNameAsync(string fileName)
    {
        await LoadCheckoutsAsync();   // the list may be stale, and a wrong match would stash onto another document

        var match = _checkouts.FirstOrDefault(c =>
            string.Equals(c.Name + c.FileExtension, fileName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Snackbar.Add(string.Format(Strings.Get("StStashNotCheckedOut"), fileName), Severity.Warning);
            return null;
        }

        // Followed from the row's own rel, never composed (ADR 0543) — the same address the Check-out tab's
        // upload button uses.
        if (Links.Href(match.Links, "working-copy") is not { } workingCopy)
        {
            Snackbar.Add(string.Format(Strings.Get("StStashNotAllowed"), fileName), Severity.Warning);
            return null;
        }

        try
        {
            return (await (await Http.PostAsJsonAsync(workingCopy, new { })).Content.ReadFromJsonAsync<WorkingCopyUploadResponse>())?.UploadUrl;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [JSInvokable]
    public async Task OnStashUploadsCompleteAsync(int stashed)
    {
        _uploading = false;
        if (stashed > 0)
        {
            Snackbar.Add(string.Format(Strings.Get("StStashUploaded"), stashed), Severity.Success);
        }

        // The stash lives on the Check-out tab, and the tree shows folders — so open the tab that can show it.
        await SetTab(Tab.Checkout);
        StateHasChanged();
    }

    // ---- Personal ▸ Intray drop (#467) --------------------------------------------------------------------
    // These live HERE because dropUpload.js holds the shell's DotNetObjectReference; the Intray tab has its own
    // pair for its own reference. Both go through IntrayUploads, so the POST exists once.

    [JSInvokable]
    public Task OnIntrayUploadsStartingAsync(int count)
    {
        _uploading = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task<string?> CreateIntrayUploadTargetAsync(string fileName) => IntrayUploads.CreateTargetAsync(fileName);

    [JSInvokable]
    public async Task OnIntrayUploadsCompleteAsync(int count)
    {
        if (count > 0)
        {
            Snackbar.Add(string.Format(Strings.Get("StUploadedToIntray"), count), Severity.Success);
        }

        // Same signal as the Intray tab's own drop — the ingest pipeline runs there and nowhere else on this
        // path, so a file dropped on the tree node would otherwise wait for the sweep worker.
        await IntrayUploads.SignalProcessedAsync();
        _uploading = false;

        // The files landed in the intray, which the tree does not show — the tree lists FOLDERS, and Intray is a
        // launcher for the tab where staging and filing actually happen. So the drop opens that tab: without it
        // the user is left looking at a tree node that cannot show what they just added (#467).
        await SetTab(Tab.Intray);
        StateHasChanged();
    }

    // ---- List-pane drop filing (ADR "List-pane drop filing") ---------------------------------------------
    // Dropping OS files onto a document row (data-drop-doc) shows the intray-style filing dialog and, per the
    // choice, files as a new version of that document or as new documents in a folder (with an optional comment).

    public record DocumentDropDecision(string Mode, string? FolderId, string? Comment);

    [JSInvokable]
    public async Task<DocumentDropDecision?> BeginDocumentDropAsync(string documentId, int fileCount)
    {
        if (!Guid.TryParse(documentId, out var docId))
        {
            return null;
        }

        // The dropped-on document is a child of the currently-open folder — that's the "in folder" target.
        var docName = _folderContents.FirstOrDefault(n => n.Id == docId)?.Name ?? "document";
        var parameters = new DialogParameters<FilingDialog>
        {
            { x => x.DocumentId, fileCount > 1 ? null : (Guid?)docId },
            { x => x.DocumentName, fileCount > 1 ? null : docName },
            { x => x.FolderId, _selectedFolder?.Id },
            { x => x.FolderName, _selectedFolder?.Name },
            { x => x.Bulk, fileCount > 1 },
        };
        var dialog = await DialogService.ShowAsync<FilingDialog>(fileCount > 1 ? $"File {fileCount} files" : $"File '{docName}'", parameters);
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: FilingDialog.FilingChoice choice })
        {
            return null;
        }

        return choice.Mode == FilingDialog.FilingMode.AsVersion
            ? new DocumentDropDecision("version", null, choice.Comment)
            : new DocumentDropDecision("folder", choice.TargetId.ToString(), choice.Comment);
    }

    [JSInvokable]
    public async Task<UploadTarget?> CreateVersionTargetAsync(string documentId, string fileName)
    {
        try
        {
            var extension = System.IO.Path.GetExtension(fileName);
            // The drop target is a bare id from JS, once per FILE — resolved once and memoised (ADR 0543/0557).
            var versionsHref = await MemoisedRelAsync(_versionsHrefByDoc, Guid.Parse(documentId), "versions");
            var versionResponse = await Http.PostAsJsonAsync(versionsHref, new { fileExtension = extension });
            if (versionResponse.StatusCode == HttpStatusCode.Conflict)
            {
                Snackbar.Add(string.Format(Strings.Get("StVersionBlocked"), fileName), Severity.Warning);
                return null;
            }
            if (versionResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                Snackbar.Add(string.Format(Strings.Get("StVersionNoPermission"), fileName), Severity.Warning);
                return null;
            }
            versionResponse.EnsureSuccessStatusCode();
            var version = await versionResponse.Content.ReadFromJsonAsync<CreateVersionResponse>();
            return new UploadTarget(documentId, version!.Id.ToString(), version.UploadUrl, FinalizeHref: Links.Href(version.Links, "self"));
        }
        catch (Exception)
        {
            Snackbar.Add(string.Format(Strings.Get("StVersionNotStarted"), fileName), Severity.Error);
            return null;
        }
    }

    [JSInvokable]
    public async Task FinalizeVersionAsync(string finalizeHref, string fileName, string? comment)
    {
        try
        {
            // The check-in comment is the new version's "why this revision" note (ADR 0528) — set on the version
            // at finalize, not posted to the chat feed. Address as in FinalizeUploadAsync above.
            var versionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            (await Http.PutAsJsonAsync(finalizeHref, new { comment = versionComment })).EnsureSuccessStatusCode();

            Snackbar.Add(string.Format(Strings.Get("StFiledVersion"), fileName), Severity.Success);
        }
        catch (Exception)
        {
            Snackbar.Add(string.Format(Strings.Get("StVersionNotFinalized"), fileName), Severity.Error);
        }
    }

    [JSInvokable]
    public async Task OnDocumentVersionsFiledAsync(string documentId)
    {
        // Reload the current folder listing, and refresh the detail/preview if the target was still selected.
        // Capture the selection first — SelectFolderAsync clears it (ClearDetail), so we re-select afterward.
        var keepSelectedId = _selectedItem?.Id;
        if (_selectedFolder is { } folder)
        {
            await SelectFolderAsync(folder);
        }

        if (Guid.TryParse(documentId, out var docId) && keepSelectedId == docId
            && _folderContents.FirstOrDefault(n => n.Id == docId) is { } node)
        {
            await SelectItemAsync(node);
        }

        StateHasChanged();
    }

    [JSInvokable]
    public Task ReportUploadFailureAsync(string fileName, string message)
    {
        Snackbar.Add(string.Format(Strings.Get("StFileError"), fileName, message), Severity.Error);
        return Task.CompletedTask;
    }

    [JSInvokable]
    // Files landed in `folderId`, NOT necessarily the folder on screen (#467). This ignored its own parameter
    // and re-listed whatever was selected, so a drop onto any other tree node uploaded and showed nothing —
    // indistinguishable from a drop that did nothing. Navigating to the target is the confirmation.
    public async Task OnUploadsCompleteAsync(string folderId)
    {
        _uploading = false;

        if (Guid.TryParse(folderId, out var target) && target != Guid.Empty && target != _selectedFolder?.Id)
        {
            await ReloadTreeAsync();       // the target gains a child, so its expander must reflect that
            await NavigateToFolderAsync(target);
            StateHasChanged();
            return;
        }

        await RefreshAsync();
        StateHasChanged();
    }
}
