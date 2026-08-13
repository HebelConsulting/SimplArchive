using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;
using SimplArchive.Localization;

namespace SimplArchive.Client.Services;

/// <summary>
/// Decides what to do when a dropped file's name is already used in the target folder.
/// </summary>
/// <remarks>
/// Before this existed the create returned 409, the shell showed a warning the user had usually stopped looking
/// at, and the file was dropped — so a drag-and-drop appeared to do nothing at all. The two things they could
/// plausibly have meant are offered instead: a new version of the document already there, or a new document under
/// a different name, either way with the filing comment they would otherwise add afterwards.
///
/// <para>It lives here rather than in the workbench page for the reason ADR 0558 gives: the shell is being
/// decomposed and may only shrink. It also keeps the composed addresses out of the page's hypermedia budget —
/// this class follows the row's <c>versions</c> rel for the case that has a row, and the caller hands it the
/// folder's children address for the two that do not.</para>
/// </remarks>
public sealed class UploadConflictResolver
{
    private readonly HttpClient _http;
    private readonly IDialogService _dialogs;
    private readonly ISnackbar _snackbar;

    public UploadConflictResolver(HttpClient http, IDialogService dialogs, ISnackbar snackbar)
    {
        _http = http;
        _dialogs = dialogs;
        _snackbar = snackbar;
    }

    /// <summary>The upload the caller should perform, or null when the user cancelled or it cannot proceed.</summary>
    /// <param name="childrenHref">The target folder's children address — used to read the siblings and, for the
    /// rename choice, to create the new document. Passed in so this class composes nothing (ADR 0543).</param>
    public async Task<ResolvedUpload?> ResolveAsync(string childrenHref, string fileName, string stem, string extension)
    {
        // The existing row comes from the folder's own listing, so the address a new version is posted to arrives
        // with it (ADRs 0555/0557) — no second lookup, and nothing rebuilt from an id.
        var listing = await _http.GetFromJsonAsync<DocumentChildrenResponse>(childrenHref);
        var children = listing?.Children ?? [];
        var existing = children.FirstOrDefault(c => string.Equals(c.Name, stem, StringComparison.OrdinalIgnoreCase));

        // Sibling names are unique across folders AND documents, so the name can be held by a FOLDER. Posting a
        // version to one would turn it into a document, so that choice is offered only against a real document.
        var parameters = new DialogParameters<Dialogs.NameConflictDialog>
        {
            { x => x.FileName, fileName },
            { x => x.SuggestedName, SuggestFreeName(stem, children) },
            { x => x.CanFileAsVersion, existing is { HasVersions: true } },
        };

        var result = await (await _dialogs.ShowAsync<Dialogs.NameConflictDialog>(
            Strings.Get("NcTitle"), parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true })).Result;

        if (result is not { Canceled: false } || result.Data is not Dialogs.NameConflictDialog.NameConflictChoice choice)
        {
            return null;
        }

        return choice.Action == "version"
            ? await AsNewVersionAsync(existing, fileName, extension, choice.Comment)
            : await AsNewDocumentAsync(childrenHref, fileName, extension, choice);
    }

    private async Task<ResolvedUpload?> AsNewVersionAsync(DocumentSummary? existing, string fileName, string extension, string comment)
    {
        if (existing is null)
        {
            // The row went away between the 409 and the listing. Rare, and refusing beats guessing which
            // document the user meant.
            _snackbar.Add(string.Format(Strings.Get("StUploadNameTaken"), fileName), Severity.Warning);
            return null;
        }

        // A missing `versions` rel means adding one is not available to this user here (ADR 0543) — say so
        // rather than composing the address anyway and meeting a 403 mid-upload.
        if (Links.Href(existing.Links, "versions") is not { } versionsHref)
        {
            _snackbar.Add(string.Format(Strings.Get("StUploadNoPermission"), fileName), Severity.Warning);
            return null;
        }

        var response = await _http.PostAsJsonAsync(versionsHref, new { fileExtension = extension });
        if (!response.IsSuccessStatusCode)
        {
            _snackbar.Add(string.Format(Strings.Get("StUploadNotStarted"), fileName), Severity.Error);
            return null;
        }

        var version = await response.Content.ReadFromJsonAsync<CreateVersionResponse>();
        return new ResolvedUpload(existing.Id, version!.Id, version.UploadUrl, comment);
    }

    private async Task<ResolvedUpload?> AsNewDocumentAsync(string childrenHref, string fileName, string extension, Dialogs.NameConflictDialog.NameConflictChoice choice)
    {
        var created = await _http.PostAsJsonAsync(childrenHref, new { name = choice.NewName });
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            // The suggested name was free when it was offered and is not now, or the user typed a taken one.
            _snackbar.Add(string.Format(Strings.Get("StUploadNameTaken"), choice.NewName), Severity.Warning);
            return null;
        }

        if (!created.IsSuccessStatusCode)
        {
            _snackbar.Add(string.Format(Strings.Get("StUploadNotStarted"), fileName), Severity.Error);
            return null;
        }

        var document = await created.Content.ReadFromJsonAsync<DocumentSummary>();
        if (Links.Href(document?.Links, "versions") is not { } versionsHref)
        {
            _snackbar.Add(string.Format(Strings.Get("StUploadNotStarted"), fileName), Severity.Error);
            return null;
        }

        var version = await _http.PostAsJsonAsync(versionsHref, new { fileExtension = extension });
        if (!version.IsSuccessStatusCode)
        {
            _snackbar.Add(string.Format(Strings.Get("StUploadNotStarted"), fileName), Severity.Error);
            return null;
        }

        var body = await version.Content.ReadFromJsonAsync<CreateVersionResponse>();
        return new ResolvedUpload(document!.Id, body!.Id, body.UploadUrl, choice.Comment);
    }

    // The create-version response: id + the presigned PUT the caller uploads to. Its own copy rather than a
    // shared one, because the shell keeps its version privately too and a service should not depend on a page.
    private sealed record CreateVersionResponse
    {
        public Guid Id { get; set; }

        public string UploadUrl { get; set; } = "";
    }

    // "Invoice" → "Invoice (2)", skipping what is already there. A starting point only: the user can type
    // anything and the server has the final say on uniqueness.
    private static string SuggestFreeName(string stem, List<DocumentSummary> siblings)
    {
        var taken = siblings.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 2; n < 1000; n++)
        {
            if (!taken.Contains($"{stem} ({n})"))
            {
                return $"{stem} ({n})";
            }
        }

        return $"{stem} ({Guid.NewGuid().ToString("N")[..6]})";
    }
}

/// <summary>Where the bytes should go, and the filing comment to set once they are there.</summary>
public sealed record ResolvedUpload(Guid DocumentId, Guid VersionId, string UploadUrl, string? Comment);
