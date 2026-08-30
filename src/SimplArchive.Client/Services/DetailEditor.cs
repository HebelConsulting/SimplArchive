using System.Net;
using System.Net.Http.Json;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;

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
    Name,

    /// <summary>The name is taken by a sibling — distinct because it is the one the user can act on.</summary>
    NameConflict,
    DocumentDate,
    OcrLanguages,
    Sensitivity,
    Tags,
    MaskAndIndexData,
    ContentsSortOrder,

    /// <summary>The caller may not set this folder's order (403), as opposed to the save simply failing.</summary>
    ContentsSortOrderForbidden,
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
/// It deliberately does NOT report to the user. Save is partial by design — each field is its own request, and
/// one refusal must not discard the rest — so the outcome is a LIST of what failed, which the caller localizes
/// and shows. Nor does it re-select, re-list or reload the tree; it says what changed and the shell decides what
/// that costs.
/// </para>
/// </remarks>
public sealed class DetailEditor(HttpClient http, DetailState detail, DetailCatalogs catalogs, DocumentActions actions)
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

            await catalogs.EnsureForEditAsync(needsOcr: detail.SysHasTiff);
            await LoadFieldsAsync(detail.EditMaskId, useCurrentValues: true);
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
        var nameChanged = false;
        var sortOrderChanged = false;
        string? duplicateClaim = null;
        try
        {
            var newName = detail.EditName.Trim();
            if (newName.Length > 0 && newName != detail.OrigName)
            {
                var etag = await actions.GetETagAsync(Href("self"));
                using var req = new HttpRequestMessage(HttpMethod.Put, Href("self")) { Content = JsonContent.Create(new { name = newName }) };
                if (etag is not null)
                {
                    req.Headers.TryAddWithoutValidation("If-Match", etag);
                }

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode) { detail.SysName = detail.OrigName = newName; nameChanged = true; }
                else if (resp.StatusCode == HttpStatusCode.Conflict) { failures.Add(DetailSaveFailure.NameConflict); }
                else { failures.Add(DetailSaveFailure.Name); }
            }

            // The address is the one the current version's row advertised when the detail loaded (`document-date`,
            // captured in DeriveSystemFields) — its absence means the row offered no such edit here (ADR 0543).
            if (detail.SysHasVersion && detail.SysDocumentDateHref is { } ddHref && detail.EditDocumentDate is { } dd && dd != detail.OrigDocumentDate)
            {
                var resp = await http.PutAsJsonAsync(ddHref, new { documentDate = dd.ToString("yyyy-MM-dd") });
                if (resp.IsSuccessStatusCode) { detail.SysDocumentDate = detail.OrigDocumentDate = dd; } else { failures.Add(DetailSaveFailure.DocumentDate); }
            }

            if (detail.SysHasTiff && !detail.EditOcrCodes.SequenceEqual(detail.OrigOcrCodes))
            {
                var resp = await http.PutAsJsonAsync(Href("ocr-languages"), new { languages = detail.EditOcrCodes });
                if (resp.IsSuccessStatusCode) { detail.SysOcrCodes = [.. detail.EditOcrCodes]; detail.OrigOcrCodes = [.. detail.EditOcrCodes]; }
                else { failures.Add(DetailSaveFailure.OcrLanguages); }
            }

            if (detail.EditSensitivityId != detail.SensitivityId)
            {
                var resp = await http.PutAsJsonAsync(Href("sensitivity"), new { labelId = detail.EditSensitivityId });
                if (resp.IsSuccessStatusCode)
                {
                    detail.SensitivityId = detail.EditSensitivityId;
                    var lbl = catalogs.Sensitivity.FirstOrDefault(l => l.Id == detail.EditSensitivityId);
                    detail.SensitivityName = lbl?.Name ?? "";
                    detail.SensitivityColor = lbl?.Color;
                    detail.SensitivityWatermark = lbl?.Watermark ?? false;
                }
                else { failures.Add(DetailSaveFailure.Sensitivity); }
            }

            // Free-form tags (ADR "Document tags"): PUT-replaces the whole set; the server normalizes/dedupes.
            var editTags = detail.EditTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 100).Distinct().ToList();
            if (!editTags.OrderBy(t => t).SequenceEqual(detail.OrigTags.OrderBy(t => t)))
            {
                var resp = await http.PutAsJsonAsync(Href("tags"), new { tags = editTags });
                if (resp.IsSuccessStatusCode)
                {
                    detail.Tags = (await resp.Content.ReadFromJsonAsync<TagsResponse>())?.Tags ?? [];
                    detail.OrigTags = [.. detail.Tags];
                }
                else { failures.Add(DetailSaveFailure.Tags); }
            }

            try
            {
                if (detail.EditMaskId is null)
                {
                    if (detail.OrigMaskId is not null)
                    {
                        (await http.DeleteAsync(Href("mask"))).EnsureSuccessStatusCode();
                    }
                }
                else
                {
                    // Fill index data first, then (re)assign the mask (which re-checks required fields).
                    var body = new { fields = detail.EditFields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.ToValues() }), confirmDuplicateClaims };
                    var indexResp = await http.PutAsJsonAsync(Href("index-data"), body);
                    if (indexResp.StatusCode == HttpStatusCode.Conflict
                        && await ProblemAsync(indexResp) is { ErrorCode: "DUPLICATE_ADDRESS_CLAIM" } problem)
                    {
                        // Not a refusal — a question, composed HERE from the response's claimedBy extension
                        // rather than surfacing the server's English prose (issue #424). The shell shows it
                        // and re-saves with the confirmation; every field already saved above was committed
                        // and its change detection makes the retry skip it.
                        duplicateClaim = string.Format(SimplArchive.Localization.Strings.Get("DupClaimBody"), problem.ClaimedBy ?? "?");
                    }
                    else
                    {
                        indexResp.EnsureSuccessStatusCode();
                        if (detail.EditMaskId != detail.OrigMaskId)
                        {
                            (await http.PutAsJsonAsync(Href("mask"), new { maskId = detail.EditMaskId })).EnsureSuccessStatusCode();
                        }
                    }
                }
            }
            catch (HttpRequestException)
            {
                failures.Add(DetailSaveFailure.MaskAndIndexData);
            }

            // A folder's contents order commits with everything else, from the same pencil (issue #408). Skipped
            // for a document, which lists nothing, and when unchanged — so an ordinary edit sends no extra request.
            if (item.IsFolder && detail.EditSortOrder != detail.SortOrder)
            {
                var resp = await http.PutAsJsonAsync(Href("contents-sort-order"), new { sortOrder = (int)detail.EditSortOrder });
                if (resp.IsSuccessStatusCode)
                {
                    detail.SortOrder = detail.EditSortOrder;
                    sortOrderChanged = true;
                }
                else
                {
                    failures.Add(resp.StatusCode == HttpStatusCode.Forbidden
                        ? DetailSaveFailure.ContentsSortOrderForbidden
                        : DetailSaveFailure.ContentsSortOrder);
                }
            }

            // Stays open on a failure (so the rejected field can be fixed) AND on the duplicate-claim
            // question — the shell asks and re-saves with the confirmation; everything already committed is
            // skipped by its own change detection on the retry.
            if (failures.Count == 0 && duplicateClaim is null)
            {
                detail.IsEditing = false;
            }

            return new DetailSaveOutcome(failures, nameChanged, sortOrderChanged, duplicateClaim);
        }
        finally
        {
            detail.Busy = false;
        }
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
