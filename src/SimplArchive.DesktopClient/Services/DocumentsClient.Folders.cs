using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// Listing a folder's contents, and the order they come back in — in its own partial.
//
// DocumentsClient.cs is on the 1000-line debt list and sat EXACTLY on its ceiling, so #854's conversion could
// not land without either raising that ceiling or paying it down. Paid down (owner-confirmed 2026-08-31),
// following the DocumentsClient.Acl.cs precedent from #877, which faced the same choice for the same reason.
//
// It is a cohesive surface: every member here reads or writes the children listing, and ReadContentsSortOrder
// is used by nothing outside it. The one thing that binds them is the ENVELOPE — the children response carries
// the rows, the folder's persisted sort order, and (since #854) whether a child may be created here, which is
// why a caller listing a folder should take all three from one response rather than asking three times
// (ADR 0557).
public sealed partial class DocumentsClient
{
    // Takes the advertised href (node.Href("children")), not a folder id (ADR 0543, issue #416). Every listing
    // that can produce a row here advertises it — the children listing and the repositories listing both do.
    public Task<List<Node>> GetChildrenAsync(string childrenHref, CancellationToken cancellationToken = default) =>
        _core.LoadPagedAsync(childrenHref, "children", ParseNode, cancellationToken);

    // The folder's persisted default contents sort order (ADR "Per-folder contents sort order") from the children
    // listing envelope — 0=Name / 1=DocumentDate / 2=Created; DocumentDate (1) when unavailable.
    // The order travels IN the children envelope, so a screen that is listing the folder anyway should call
    // GetFolderContentsAsync and read both from one response. This overload is for the callers that want only
    // the number (a VM check), and it asks for a single row rather than a page to get it.
    public async Task<int> GetContentsSortOrderAsync(string childrenHref, CancellationToken cancellationToken = default) =>
        ReadContentsSortOrder(await _core.Http.GetFromJsonAsync<JsonElement>(childrenHref + "?limit=1", cancellationToken));

    // Sets the folder's persisted default contents sort order (CanEditIndexData-gated).
    public async Task SetContentsSortOrderAsync(string contentsSortOrderHref, int sortOrder, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(contentsSortOrderHref, new { sortOrder }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiActionException($"Could not set the contents sort order ({(int)response.StatusCode}).");
        }
    }

    internal static int ReadContentsSortOrder(JsonElement envelope) =>
        envelope.TryGetProperty("contentsSortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 1;

    /// <summary>
    /// A folder's contents AND its persisted contents order, from the one listing that already carries both.
    /// Following rels must not turn one screen into N requests, and the order travelling in the children
    /// envelope is precisely so a client does not have to ask for it separately (ADR 0543, issue #416).
    /// Since #854 it carries a THIRD answer from that same envelope — whether the caller may create a child
    /// here — which used to be the `create-child` rel on the folder's own resource.
    /// </summary>
    public async Task<(List<Node> Children, int SortOrder, bool CanCreateChildren)> GetFolderContentsAsync(string childrenHref, CancellationToken cancellationToken = default)
    {
        var sortOrder = 1;
        var canCreateChildren = false;
        var first = true;
        var children = await _core.LoadPagedAsync(childrenHref, "children", ParseNode, cancellationToken, page =>
        {
            if (first)
            {
                sortOrder = ReadContentsSortOrder(page);
                canCreateChildren = page.TryGetProperty("canCreateChildren", out var c) && c.ValueKind == JsonValueKind.True;
                first = false;
            }
        });

        return (children, sortOrder, canCreateChildren);
    }
}
