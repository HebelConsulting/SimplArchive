using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;

using SimplArchive.Presentation;

namespace SimplArchive.Client.Services;

/// <summary>Which part of a pane-level save the server refused. The caller names them (see the remarks).</summary>
/// <remarks>
/// Returned rather than rendered because the editor has no business holding UI text — and because the strings it
/// replaced were bare English literals (<c>"document date"</c>, <c>"OCR languages"</c>) joined into a localized
/// sentence, so a German user read a German apology about an English field. Naming the failures instead of
/// spelling them makes that the caller's problem, which is where the resources are.
/// </remarks>
public enum DetailSaveFailure
{
    /// <summary>The one save was refused. There is no per-aspect case any more, because there is no per-aspect
    /// REQUEST any more (ADR 0794) — the edit either lands whole or not at all.</summary>
    Save,

    /// <summary>The name is taken by a sibling — distinct because it is the one the user can act on.</summary>
    NameConflict,

    /// <summary>
    /// Somebody else wrote this document while the form was open (412).
    /// </summary>
    /// <remarks>
    /// Reachable for the first time. The precondition is now the tag the form was LOADED with, so it can
    /// actually detect a concurrent edit; the previous code re-read the tag immediately before writing, which
    /// asked "has it changed in the last five milliseconds?" and was therefore always satisfied.
    /// </remarks>
    ChangedElsewhere,
}

/// <summary>
/// What a save changed, for the shell to finish. Everything here is work the editor cannot do: reloading the
/// tree, re-listing a folder, re-selecting a row.
/// </summary>
/// <param name="Failures">Empty on success. Non-empty means the edit STAYS OPEN so the rejected field can be fixed.</param>
/// <param name="NameChanged">The tree and the open listing still show the old name.</param>
/// <param name="ContentsSortOrderChanged">If this folder is the open one, its listing is still in the old order.</param>
public sealed record DetailSaveOutcome(
    IReadOnlyList<DetailSaveFailure> Failures,
    bool NameChanged,
    bool ContentsSortOrderChanged,
    // The 409 DUPLICATE_ADDRESS_CLAIM message, verbatim — it names the other mailbox, which is what the
    // confirm dialog must show (#703). Null on every other outcome; the shell asks and re-saves with
    // confirmDuplicateClaims. Not a DetailSaveFailure: a failure is reported and left, this is a QUESTION.
    string? DuplicateClaim = null)
{
    public bool Saved => Failures.Count == 0;
}

/// <summary>
/// The index-data pane's edit lifecycle — begin, change, save, cancel — for the one subject
/// <see cref="DetailState.Node"/> names (ADR 0278: one pencil commits name, date, OCR, mask, index data, tags,
/// sensitivity and a folder's contents order together).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the page (ADR 0558) because it is the largest thing in it that is not rendering: a 150-line
/// save with a per-field partial-failure protocol, sitting between the markup it has nothing to do with. The
/// working copy stays in <see cref="DetailState"/> rather than moving here — that is what survives the tab
/// switch that disposes the pane, and a half-filled index form is the clearest case of state a user is annoyed
/// to lose.
/// </para>
/// <para>
/// It deliberately does NOT report to the user: the outcome says what failed and the caller localizes and shows
/// it. Nor does it re-select, re-list or reload the tree; it says what changed and the shell decides what that
/// costs.
///
/// Save is no longer partial. It is ONE request over the whole detail (ADR 0794), so the edit lands whole or
/// not at all — where before each field was its own write and a refusal part-way left the document half-saved
/// with no transaction and, for seven of the eight writes, no precondition either.
/// </para>
/// </remarks>
public sealed class DetailEditor(HttpClient http, DetailState detail, DetailCatalogs catalogs)
{
    /// <summary>
    /// Whether the pane can enter edit: there is a subject, no edit is already open, and — the part that was
    /// missing — the SERVER said this caller may change it.
    /// </summary>
    /// <remarks>
    /// This was `Node is not null && !IsEditing`, with no server input at all, so the pencil rendered for a
    /// read-only caller and the refusal arrived at Save (#859). It sat directly beside `CanManagePermissions`,
    /// which is a real server flag — the file held both the pattern and its counter-example.
    ///
    /// `CanEditIndexData` is the right the `PUT` on the document's own address enforces, so the gate and the
    /// refusal are the same fact and cannot drift apart.
    /// </remarks>
    public bool CanEdit => detail.Node is not null && !detail.IsEditing && detail.CanEditIndexData;

    private string Href(string rel) => Links.Required(detail.Links, rel);

    /// <summary>
    /// Opens the edit: stages every field's current value as the working copy, snapshots the originals for
    /// change detection, and loads the catalogues the form needs.
    /// </summary>
    /// <remarks>
    /// Lets a fetch failure propagate rather than swallowing it. The caller is the one that can say so — and the
    /// pane stays in read mode either way, because <see cref="DetailState.IsEditing"/> is set last, after
    /// everything the form needs has arrived.
    /// </remarks>
    public async Task BeginAsync()
    {
        if (detail.Node is null)
        {
            return;
        }

        detail.Busy = true;
        try
        {
            detail.EditName = detail.OrigName = detail.SysName;
            detail.EditDocumentDate = detail.OrigDocumentDate = detail.SysDocumentDate;
            detail.EditDocumentTime = detail.OrigDocumentTime = detail.SysDocumentTime;
            detail.EditOcrCodes = [.. detail.SysOcrCodes];
            detail.OrigOcrCodes = [.. detail.SysOcrCodes];
            detail.EditMaskId = detail.OrigMaskId = detail.MaskId;
            detail.EditSensitivityId = detail.SensitivityId;
            detail.EditTags = [.. (detail.Tags ?? [])];
            detail.OrigTags = [.. (detail.Tags ?? [])];
            detail.EditNewTag = null;
            // One pencil commits everything the pane shows, so a folder's contents order is staged alongside its
            // mask and index fields rather than hiding behind an Edit button of its own.
            detail.EditSortOrder = detail.SortOrder;

            await catalogs.EnsureForEditAsync(needsOcr: detail.SysOcrCandidate);
            await LoadFieldsAsync(detail.EditMaskId, useCurrentValues: true);

            // Machine proposals (ABI 0.11, ADR 0769): the rels rode in with the document; the items are
            // fetched NOW, so the candidate list is as fresh as the form it serves. Keyed by the field
            // each fills — the pane's picker renders beside that field's editor.
            detail.Proposals = [];
            foreach (var link in detail.Links?.Where(l => l.Key.StartsWith("machine-proposal:", StringComparison.Ordinal)) ?? [])
            {
                var proposal = await http.GetFromJsonAsync<ProposalResponse>(link.Value);
                if (proposal?.FillsField is { Length: > 0 } field)
                {
                    detail.Proposals[field] = new DetailState.ProposalOffer(
                        proposal.Label ?? string.Empty,
                        [.. (proposal.Items ?? []).Select(i => new DetailState.ProposalOfferItem(i.Value ?? string.Empty, i.Label ?? string.Empty, i.Detail))]);
                }
            }

            // The read that fills the form, and the tag a save is measured against (ADR 0794). Taken HERE
            // because the user cannot have edited anything before clicking the pencil, so "as it was when I
            // opened the form" is exactly what this asserts.
            using (var response = await http.GetAsync(Href("detail")))
            {
                response.EnsureSuccessStatusCode();
                detail.EditBaseline = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                detail.EditEtag = response.Headers.ETag?.Tag;
            }

            detail.IsEditing = true;
        }
        finally
        {
            detail.Busy = false;
        }
    }

    /// <summary>Discards the working copy by simply leaving edit mode — which is what keeping it separate buys.</summary>
    public void Cancel() => detail.IsEditing = false;

    /// <summary>
    /// Switches the form to another mask, which REPLACES the fields with that mask's own, empty. Not a merge:
    /// picking a different mask is a statement about what this document is, not an edit to the values under it.
    /// </summary>
    public Task ChangeMaskAsync(Guid? maskId)
    {
        detail.EditMaskId = maskId;
        return LoadFieldsAsync(maskId, useCurrentValues: false);
    }

    /// <summary>Appends an OCR language, or removes it if already picked — the order is the recognition order (ADR 0272).</summary>
    public void ToggleOcr(string code)
    {
        if (!detail.EditOcrCodes.Remove(code))
        {
            detail.EditOcrCodes.Add(code);
        }
    }

    /// <summary>Adds the typed tag as a chip. A no-op for a blank, over-long or duplicate value.</summary>
    public void AddTypedTag()
    {
        var t = (detail.EditNewTag ?? "").Trim().ToLowerInvariant();
        if (t.Length is > 0 and <= 100 && !detail.EditTags.Contains(t))
        {
            detail.EditTags.Add(t);
        }

        detail.EditNewTag = null;
    }

    /// <summary>Catalogue tags not yet on this document, narrowed by what has been typed.</summary>
    public IEnumerable<string> SuggestTags(string? typed)
    {
        var pool = catalogs.TagNames.Where(t => !detail.EditTags.Contains(t));
        return string.IsNullOrWhiteSpace(typed)
            ? pool
            : pool.Where(t => t.Contains(typed.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadFieldsAsync(Guid? maskId, bool useCurrentValues)
    {
        detail.EditFields.Clear();
        if (maskId is not { } id)
        {
            return;
        }

        // The mask id came from the picker, and the picker's rows are the catalogue listing — whose rows carry
        // their own address (ADR 0555).
        //
        // The catalogue carries only the masks a user may freely CHOOSE (#671), so for the document's OWN mask
        // it may have nothing — a Mailbox, a Calendar, an Addressbook, a repository. That is not "no fields to
        // offer": it is a different question, answered by the document itself, whose mask resource advertises
        // where its definitions live (#729, ADR 0688). Without this the editor opened on a typed folder with no
        // boxes at all, which is how a Mailbox's address list became unsettable from the UI.
        var maskHref = Links.Href(catalogs.Masks.FirstOrDefault(m => m.Id == id)?.Links, "self")
                       ?? (id == detail.MaskId ? detail.MaskDefinitionHref : null);
        if (maskHref is null)
        {
            return;
        }

        var fields = await http.GetFromJsonAsync<MaskFieldsResponse>(maskHref);
        var valuesByName = useCurrentValues
            ? (detail.IndexData ?? []).ToDictionary(f => f.FieldName, f => f.Values)
            : new Dictionary<string, List<string>>();

        foreach (var f in fields?.Fields ?? [])
        {
            detail.EditFields.Add(EditField.Create(f, valuesByName.TryGetValue(f.Name, out var v) ? v : [], catalogs.MayRouteMail));
        }
    }

    /// <summary>
    /// Persists only what changed, one field at a time, collecting refusals rather than stopping at the first
    /// (ADR 0278). A non-empty <see cref="DetailSaveOutcome.Failures"/> leaves the edit OPEN.
    /// </summary>
    public async Task<DetailSaveOutcome> SaveAsync(bool confirmDuplicateClaims = false)
    {
        if (detail.Node is not { } item)
        {
            return new DetailSaveOutcome([], false, false);
        }

        detail.Busy = true;
        var failures = new List<DetailSaveFailure>();
        string? duplicateClaim = null;
        try
        {
            var newName = detail.EditName.Trim();
            var nameChanged = newName.Length > 0 && newName != detail.OrigName;

            var editTags = detail.EditTags
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length is > 0 and <= 100)
                .Distinct()
                .ToList();
            var sortOrderChanged = item.IsFolder && detail.EditSortOrder != detail.SortOrder;

            // ONE request for the whole pencil (ADR 0794). It is a PUT of the full detail, so every aspect is
            // stated: the edited ones from the form, the rest echoed from the baseline this edit opened with —
            // an omitted aspect would be a request to CLEAR it, not to leave it alone.
            var body = Body(newName.Length > 0 ? newName : null, editTags, confirmDuplicateClaims, sortOrderChanged);

            using var request = new HttpRequestMessage(HttpMethod.Put, Href("detail")) { Content = JsonContent.Create(body) };
            if (detail.EditEtag is { } etag)
            {
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            }

            var response = await http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Conflict
                && await ProblemAsync(response) is { ErrorCode: "DUPLICATE_ADDRESS_CLAIM" } claim)
            {
                // Not a refusal — a question, composed HERE from the response's claimedBy extension rather than
                // surfacing the server's English prose (issue #424). In one transaction NOTHING was written by
                // the attempt that asked it, so the retry is simply this same request with the flag set; the
                // old per-aspect path had already committed the earlier fields and relied on change detection
                // to skip them.
                duplicateClaim = string.Format(SimplArchive.Localization.Strings.Get("DupClaimBody"), claim.ClaimedBy ?? "?");

                return new DetailSaveOutcome(failures, false, false, duplicateClaim);
            }

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // Somebody else wrote while this form was open. Reachable for the first time (ADR 0794): the
                // tag is the one the form was LOADED with, where the old code re-read it moments before writing
                // and so asked a question whose answer was always yes.
                failures.Add(DetailSaveFailure.ChangedElsewhere);

                return new DetailSaveOutcome(failures, false, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                failures.Add(response.StatusCode == HttpStatusCode.Conflict
                    ? DetailSaveFailure.NameConflict
                    : DetailSaveFailure.Save);

                return new DetailSaveOutcome(failures, false, false);
            }

            var saved = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Adopt(saved);
            detail.IsEditing = false;

            return new DetailSaveOutcome(failures, nameChanged, sortOrderChanged);
        }
        catch (HttpRequestException)
        {
            failures.Add(DetailSaveFailure.Save);

            return new DetailSaveOutcome(failures, false, false);
        }
        finally
        {
            detail.Busy = false;
        }
    }

    /// <summary>
    /// The full intended detail: what the user edited, over what this edit opened with.
    /// </summary>
    private object Body(string? name, List<string> tags, bool confirmDuplicateClaims, bool sortOrderChanged)
    {
        var baseline = detail.EditBaseline;

        string? Text(string property) =>
            baseline?.TryGetProperty(property, out var value) == true && value.ValueKind != JsonValueKind.Null
                ? value.GetString()
                : null;

        return new
        {
            name = name ?? Text("name"),
            documentDate = detail.EditDocumentDate?.ToString("yyyy-MM-dd") ?? Text("documentDate"),
            documentTime = detail.EditDocumentTime ?? Text("documentTime"),
            ocrLanguages = detail.EditOcrCodes,
            sensitivityLabelId = detail.EditSensitivityId,
            tags,
            fields = detail.EditFields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.ToValues() }),
            maskId = detail.EditMaskId,
            // Folder-only, and only when it actually moved — a document has no contents to order, and the
            // server treats null as "leave it alone" rather than as the enum's first value.
            contentsSortOrder = sortOrderChanged ? (int?)detail.EditSortOrder : null,
            confirmDuplicateClaims,
        };
    }

    /// <summary>Takes the saved detail as the pane's new truth, so nothing is left describing the old one.</summary>
    private void Adopt(JsonElement saved)
    {
        string? Text(string property) =>
            saved.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

        detail.SysName = detail.OrigName = Text("name") ?? detail.SysName;
        detail.OrigDocumentDate = detail.SysDocumentDate = detail.EditDocumentDate;
        detail.OrigDocumentTime = detail.SysDocumentTime = detail.EditDocumentTime;
        detail.SysOcrCodes = [.. detail.EditOcrCodes];
        detail.OrigOcrCodes = [.. detail.EditOcrCodes];
        detail.SensitivityId = detail.EditSensitivityId;
        var label = catalogs.Sensitivity.FirstOrDefault(l => l.Id == detail.EditSensitivityId);
        detail.SensitivityName = label?.Name ?? string.Empty;
        detail.SensitivityColor = label?.Color;
        detail.SensitivityWatermark = label?.Watermark ?? false;
        detail.Tags = saved.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? [.. tags.EnumerateArray().Select(t => t.GetString() ?? string.Empty)]
            : detail.Tags;
        detail.OrigTags = [.. (detail.Tags ?? [])];
        detail.SortOrder = detail.EditSortOrder;
        detail.EditEtag = Text("etag") is { } fresh ? $"\"{fresh}\"" : detail.EditEtag;
    }

    private sealed record ProblemBody(string? ErrorCode, string? ClaimedBy);

    // Reads an RFC 7807 body's errorCode + extensions; null when the body is not problem-shaped.
    private static async Task<ProblemBody?> ProblemAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemBody>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
