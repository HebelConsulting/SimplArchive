using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

// The document ACL surface — manage access (ADR "Manage-access UI for document/folder ACLs"), in its own
// partial. DocumentsClient.cs is on the 1000-line debt list, and #877 needed the collection's grantable-rights
// cap plumbed through it; extracting the surface pays that debt DOWN rather than raising the ceiling again
// (owner-confirmed 2026-08-30, the same move as DocumentsClient.Tags.cs and the .Export.cs precedent).
//
// It is a cohesive surface: everything here serves one dialog, and ReadRights is used by nothing else.
public sealed partial class DocumentsClient
{
    // Reads the document FIRST and works outwards from what it advertises (ADR 0543, issue #416). The order
    // matters: `acl-entries` is gated on CanManagePermissions, so its ABSENCE is the answer the dialog needs —
    // it no longer discovers "you may not manage access" by sending a request designed to be refused with a 403.
    // The collection then hands over `grantable-principals`, so the picker is one link away rather than a second
    // path assembled here. The whole call is best-effort in the same direction it always was: any failure reads
    // as "no rights", which hides affordances rather than offering ones that cannot work.
    public async Task<AclInfo> GetAclAsync(string documentSelfHref, CancellationToken cancellationToken = default)
    {
        JsonElement doc;
        try
        {
            doc = await _core.Http.GetFromJsonAsync<JsonElement>(documentSelfHref, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new AclInfo(true, false, [], [], null);
        }

        var docLinks = ApiCore.ParseLinks(doc) ?? new Dictionary<string, string>();
        if (!docLinks.TryGetValue("acl-entries", out var aclHref))
        {
            return new AclInfo(true, false, [], [], null);
        }

        var breaksInheritance = doc.TryGetProperty("breaksInheritance", out var bi) && bi.ValueKind == JsonValueKind.True;
        docLinks.TryGetValue("acl-inheritance", out var inheritanceHref);

        using var listResponse = await _core.Http.GetAsync(aclHref, cancellationToken);
        if (listResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            return new AclInfo(true, false, [], [], null);
        }

        listResponse.EnsureSuccessStatusCode();

        var entries = new List<AclEntryInfo>();
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (listJson.TryGetProperty("entries", out var es))
        {
            foreach (var e in es.EnumerateArray())
            {
                entries.Add(new AclEntryInfo(
                    e.GetProperty("principalType").GetString() ?? "",
                    e.GetProperty("principalId").GetGuid(),
                    ReadRights(e),
                    ApiCore.ParseLinks(e)));
            }
        }

        // Read with the SAME parser the entries use: one vocabulary, one place to update (#877).
        var grantable = listJson.TryGetProperty("grantableRights", out var gr) ? ReadRights(gr) : null;

        var principals = new List<GrantablePrincipalInfo>();
        var pj = await _core.Http.GetFromJsonAsync<JsonElement>(ApiCore.RequireRel(listJson, "grantable-principals", "The ACL collection"), cancellationToken);
        if (pj.TryGetProperty("principals", out var ps))
        {
            foreach (var p in ps.EnumerateArray())
            {
                principals.Add(new GrantablePrincipalInfo(
                    p.GetProperty("type").GetString() ?? "",
                    p.GetProperty("id").GetGuid(),
                    p.GetProperty("name").GetString() ?? "",
                    ApiCore.ParseLinks(p)));
            }
        }

        return new AclInfo(false, breaksInheritance, entries, principals, inheritanceHref, grantable);
    }

    public sealed record EffectiveAccessInfo(string? InheritedFrom, List<EffectiveAccessEntryInfo> Entries);

    public sealed record EffectiveAccessEntryInfo(string Type, Guid Id, string Name, string Access, string? ViaGroup, AclRights Rights);

    // The resolved "who can actually access this" view (ADR 0488): effective grants resolved to people (groups
    // expanded to members, tenant admins flagged).
    // `effective` is a rel on the ACL COLLECTION, so the collection is read first — one hop that also answers
    // "may I see this at all" by whether the document advertised `acl-entries` (ADR 0543).
    public async Task<EffectiveAccessInfo> GetEffectiveAccessAsync(string aclEntriesHref, CancellationToken cancellationToken = default)
    {
        var collection = await _core.Http.GetFromJsonAsync<JsonElement>(aclEntriesHref, cancellationToken);
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(ApiCore.RequireRel(collection, "effective", "The ACL collection"), cancellationToken);

        var entries = new List<EffectiveAccessEntryInfo>();
        if (json.TryGetProperty("entries", out var es))
        {
            foreach (var e in es.EnumerateArray())
            {
                entries.Add(new EffectiveAccessEntryInfo(
                    e.GetProperty("type").GetString() ?? "",
                    e.GetProperty("id").GetGuid(),
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("access").GetString() ?? "",
                    e.TryGetProperty("viaGroup", out var vg) && vg.ValueKind == JsonValueKind.String ? vg.GetString() : null,
                    ReadRights(e)));
            }
        }

        var inheritedFrom = json.TryGetProperty("inheritedFrom", out var inf) && inf.ValueKind == JsonValueKind.String ? inf.GetString() : null;
        return new EffectiveAccessInfo(inheritedFrom, entries);
    }

    // Break (copy inherited grants down) / restore (discard own grants) ACL inheritance (ADR 0486 follow-up).
    // Takes the advertised href rather than composing one (ADR 0543); the caller only has it when the server
    // offered the action.
    public async Task SetInheritanceAsync(string href, bool breaksInheritance, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PutAsJsonAsync(href, new { breaksInheritance }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    private static AclRights ReadRights(JsonElement e) => new(
        e.GetProperty("canSee").GetBoolean(),
        e.GetProperty("canReadContent").GetBoolean(),
        e.GetProperty("canEditContent").GetBoolean(),
        e.GetProperty("canEditIndexData").GetBoolean(),
        e.GetProperty("canCreateSubItems").GetBoolean(),
        e.GetProperty("canDelete").GetBoolean(),
        e.GetProperty("canMove").GetBoolean(),
        e.GetProperty("canAnnotate").GetBoolean(),
        e.GetProperty("canManagePermissions").GetBoolean());

    public async Task RevokeAclEntryAsync(AclEntryInfo entry, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(ApiCore.RequireHref(entry, "self"), cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }

    // Writes the rights at the address the ROW gave us — `grant` on a principal being added, `self` on an
    // entry already there; RevokeAclEntryAsync above DELETEs that same `self`. One rel, the method says which
    // action (ADR 0719); `grant` is not its verb pair — it is emitted only while there is no entry yet.
    public async Task SetAclEntryAsync(IAdvertisesLinks row, AclRights rights, CancellationToken cancellationToken = default)
    {
        var href = row.Href("grant") ?? row.Href("self")
            ?? throw new InvalidOperationException($"The row '{row.Name}' advertised neither 'grant' nor 'self' — you may not change its access (ADR 0543/0555).");
        using var response = await _core.Http.PutAsJsonAsync(href, rights, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException(Strings.Get("MaInsufficientRights"));
        }

        await SimplArchiveApiClient.ThrowIfProblemAsync(response, Strings.Get("MaLoadFailed"), cancellationToken);
    }
}
