using System.Net.Http.Json;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// Fills <see cref="DetailState"/> for whatever the pane is describing — a document row, a folder row, or the
/// open folder (ADR 0558's decomposition of the workbench shell; issue #408 made those one pane).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the shell because it is a responsibility, not a fragment of page code: it owns the sequence
/// of reads that turns a row into a described subject, and — more importantly — it owns the question of WHICH
/// load is still wanted. The shell keeps only what is coupled to rendering: the layout interop, the preview,
/// and the comment thread.
/// </para>
/// <para>
/// <b>Every load carries a token (#784, ADR 0559).</b> The pane is ONE object and this sequence is a dozen
/// awaits long, so two overlapping loads do not take turns — they interleave field by field, and the one that
/// STARTED first can resume last and overwrite the newer subject. Measured before the guard existed: opening a
/// folder and clicking a document inside it left the pane describing the FOLDER on 15 % of attempts, and it
/// stayed wrong, because nothing re-triggers a load the user did not ask for. Cancelling the HTTP calls would
/// not have helped — the damage is the ASSIGNMENTS, not the requests — so a superseded load simply stops
/// writing.
/// </para>
/// </remarks>
public sealed class DetailLoader(
    HttpClient http,
    DetailState detail,
    DetailCatalogs catalogs,
    DocumentActions actions,
    AnnotationEditor annotations)
{
    private int _epoch;

    /// <summary>Claims the pane for a new load. The caller passes the token back to every staleness check.</summary>
    public int Begin() => ++_epoch;

    /// <summary>True once a later load has claimed the pane — the holder of this token must stop writing.</summary>
    public bool Superseded(int token) => token != _epoch;

    /// <summary>
    /// Reads the subject and populates <see cref="DetailState"/>. Returns what the SHELL still has to do with
    /// the result — the render-coupled tail it alone can perform.
    /// </summary>
    public async Task<DetailLoadResult> LoadAsync(BrowseNode item, int token)
    {
        // Set unconditionally, unlike the version-derived fields: a folder has a name to show but no version to
        // derive one from, which is exactly why it used to fall out of this pane entirely.
        detail.SysName = detail.OrigName = item.Name;

        try
        {
            // The document resource first: it carries the computed retention schedule (ADR "Retention policies"),
            // the sensitivity label (ADR "Data classification"), AND the rels for everything else this pane
            // loads — so the calls below follow advertised addresses instead of composing them (ADR 0543, #416).
            // DocumentAddress, not `self`: on a REPOSITORY row `self` is the repository view, whose rels are a
            // different set — reading it here silently cost the pane its document rels (contents-sort-order
            // among them). ADR 0200 says a repository IS a document; only the server can say at which address.
            if (DocumentActions.DocumentAddress(item) is not { } detailSelfHref)
            {
                return DetailLoadResult.Nothing;
            }

            var document = await http.GetFromJsonAsync<DocumentDetailResponse>(detailSelfHref);
            if (Superseded(token))
            {
                return DetailLoadResult.Nothing;
            }

            detail.Links = Links.RelMap(document?.Links);
            detail.MaskId = null;

            // mask + index-data come from the ROW's own rels — the listing advertises them, so no second fetch.
            detail.ApplyMask(await http.GetFromJsonAsync<MaskResponse>(RowOrDetailHref(item, "mask")));

            var index = await http.GetFromJsonAsync<IndexDataResponse>(RowOrDetailHref(item, "index-data"));
            if (Superseded(token))
            {
                return DetailLoadResult.Nothing;
            }

            detail.IndexData = index?.Fields ?? [];

            // Free-form tags (ADR "Document tags") — advertised on the resource, not on a listing row.
            detail.Tags = (await http.GetFromJsonAsync<TagsResponse>(DetailHref("tags")))?.Tags ?? [];
            detail.Retention = document?.Retention;
            detail.SensitivityId = document?.SensitivityLabelId;
            detail.SensitivityName = document?.SensitivityLabelName ?? "";
            detail.SensitivityColor = document?.SensitivityLabelColor;
            detail.SensitivityWatermark = document?.SensitivityWatermark ?? false;
            detail.CanManagePermissions = document?.CanManagePermissions ?? false;
            // Cleared to FALSE with the rest when the subject changes (ADR 0559): during a load the honest
            // answer is "not available to you, here, now", and inheriting the previous document's answer is a
            // claim about the wrong object.
            detail.CanEditIndexData = document?.CanEditIndexData ?? false;
            detail.BreaksInheritance = document?.BreaksInheritance ?? false;

            // A FOLDER's own contents order, which now travels with its details — the pane for a child folder is
            // opened from its parent's listing, where the child's setting was never fetched (issue #408).
            detail.SortOrder = document?.ContentsSortOrder ?? FolderContentsSortOrder.Name;

            // Captured from the resource's own rel (ADR 0543/0546) — the external-links dialog takes the href
            // rather than composing one, and its absence simply means the affordance isn't offered.
            detail.ExternalLinksHref = Links.Href(document?.Links, "external-links");
            await catalogs.EnsureSensitivityAsync();

            // Whether the current user follows this document (ADR "Document subscriptions").
            detail.Subscribed = await actions.IsSubscribedAsync(DetailHref("subscription"));

            var versions = await http.GetFromJsonAsync<VersionListResponse>(DetailHref("versions"));
            if (Superseded(token))
            {
                return DetailLoadResult.Nothing;
            }

            var confirmed = versions?.Versions?.Where(v => v.Status == "Confirmed").ToList() ?? [];
            var latest = VersionListResponse.PickCurrent(confirmed, versions?.CurrentVersionId);
            var result = DetailLoadResult.Nothing with { Loaded = true };
            if (latest is not null)
            {
                annotations.UseCollection(Links.Href(latest.Links, "annotations"));
                result = result with
                {
                    PreviewUrl = http.Absolute(Links.Href(latest.Links, "preview")),
                    DownloadUrl = http.Absolute(Links.Href(latest.Links, "download")),
                    TextLayoutUrl = Links.Href(latest.Links, "text-layout"),
                    Converted = latest.PreviewConverted,
                    HasVersion = true,
                };
            }

            DeriveSystemFields(item, confirmed, versions?.CurrentVersionId);

            // The transitions the pane may offer (#691) — followed from the current version's own workflow rel,
            // and skipped for the states that offer nothing (see WorkflowTransitionsAsync: ADR 0557).
            detail.WorkflowLinks = await actions.WorkflowTransitionsAsync(
                detail.SysWorkflowStatus, latest is null ? null : Links.Href(latest.Links, "workflow"));

            return Superseded(token) ? DetailLoadResult.Nothing : result;
        }
        catch (HttpRequestException)
        {
            detail.IndexData ??= [];
        }
        catch (InvalidOperationException)
        {
            // A rel this pane expected was not advertised. Degrade the PANE — never let it escape: the caller
            // runs on a render path, so an unhandled exception blanks the whole workbench, list and tree
            // included, not just the part that could not load. That is exactly what happened when tree roots
            // were built without their links (#416): one missing rel took the entire page down.
            detail.IndexData ??= [];
        }

        return DetailLoadResult.Nothing;
    }

    private void DeriveSystemFields(BrowseNode item, List<VersionResponse> confirmed, Guid? currentVersionId)
    {
        var current = VersionListResponse.PickCurrent(confirmed, currentVersionId);
        if (current is null)
        {
            return; // a folder or a document with no confirmed version yet
        }

        static bool IsTiff(string? key) => key is not null
            && (key.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase));

        var tiff = confirmed.Where(v => IsTiff(v.ObjectKey)).OrderByDescending(v => v.VersionNumber ?? 0).FirstOrDefault();

        detail.SysHasVersion = true;
        detail.SysWorkflowStatus = current.WorkflowStatus;
        detail.SysCurrentVersionId = current.Id;
        detail.SysDocumentDateHref = Links.Href(current.Links, "document-date");
        detail.SysCurrentVersion = current.VersionNumber;
        detail.VersionCount = confirmed.Count;
        detail.SysName = item.Name;
        detail.SysFileExtension = current.FileExtension ?? "";
        detail.SysDocumentDate = DateTime.TryParse(current.DocumentDate, out var d) ? d.Date : null;
        detail.SysCreated = current.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        detail.SysCreatedBy = current.CreatedByName ?? "";
        detail.SysHasTiff = tiff is not null;
        detail.SysOcrCodes = string.IsNullOrWhiteSpace(tiff?.OcrLanguages)
            ? []
            : tiff!.OcrLanguages!.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private string DetailHref(string rel) => Links.Required(detail.Links, rel);

    // The row's own advertised rel when it has one — no extra call — else the OPEN RESOURCE's, which this pane
    // has already fetched. Not every BrowseNode comes from a listing: a reference row, or a node built from an
    // id when navigating to a referencing folder, carries nothing. Those are exactly the "I hold an id, not a
    // resource" case, and the answer is to follow the resource we just fetched — never to compose (ADR 0543).
    private string RowOrDetailHref(BrowseNode item, string rel) =>
        item.Links is not null && item.Links.TryGetValue(rel, out var href) ? href : DetailHref(rel);
}

/// <summary>What the shell still has to do once the pane's fields are populated — the render-coupled tail.</summary>
public readonly record struct DetailLoadResult(
    bool Loaded,
    string? PreviewUrl,
    string? TextLayoutUrl,
    string? DownloadUrl,
    bool Converted,
    bool HasVersion)
{
    public static DetailLoadResult Nothing => new(false, null, null, null, false, false);
}
