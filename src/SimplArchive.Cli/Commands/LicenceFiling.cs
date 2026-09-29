using System.Text.Json;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Files the licence bytes as a document under a chosen parent, following rels the whole way (#1474).
/// </summary>
/// <remarks>
/// Its own class because it is the part with steps: the API never proxies file bytes, so content arrives in
/// THREE acts — ask for a version, put the bytes straight into object storage, then confirm the version.
/// Keeping that out of the command leaves the command readable as what it does rather than how.
/// </remarks>
internal static class LicenceFiling
{
    internal static async Task<Guid> FileAsync(
        SimplArchiveApi api,
        Hypermedia hypermedia,
        Guid parentId,
        string name,
        byte[] licence,
        CancellationToken cancellationToken)
    {
        var parent = await ResolveAsync(api, hypermedia, parentId, cancellationToken);

        // The capability, not a rel. `children` is advertised to anyone who may READ the collection, while
        // creating needs CanCreateSubItems — so ADR 0719 puts the narrower right on the resource as a flag,
        // and gating on the rel's presence would POST into a 403 the caller could have been told about.
        if (parent.TryGetProperty("canCreateChildren", out var permitted)
            && permitted.ValueKind == JsonValueKind.False)
        {
            throw new CliException(
                $"Document {parentId:D} does not accept new children for this caller — either the right to "
                + "create sub-items is missing, or that folder's mask does not admit a plain document. "
                + "File the licence in a repository or an ordinary folder.");
        }

        var childrenHref = Hypermedia.Href(Links(parent), "children", $"Document {parentId:D}");
        var created = await api.PostAsync(childrenHref, new { name }, cancellationToken);
        var documentId = created.TryGetProperty("id", out var id) && id.TryGetGuid(out var value)
            ? value
            : throw new CliException("Creating the licence document answered without an id.");

        // Follow `versions` from the create response when it advertises it, and re-read the document when it
        // does not. One extra GET on a one-shot administrative command is worth not depending on which link
        // set a create happens to return — and re-reading through the root's `document` rel is the sanctioned
        // id-to-resource turn, not a composed path.
        var versionsHref = Links(created).TryGetValue("versions", out var advertised)
            ? advertised
            : Hypermedia.Href(
                Links(await ResolveAsync(api, hypermedia, documentId, cancellationToken)),
                "versions", $"Document {documentId:D}");

        var version = await api.PostAsync(versionsHref, new { fileExtension = ".json" }, cancellationToken);
        var versionId = version.GetProperty("id").GetGuid();
        var uploadUrl = version.GetProperty("uploadUrl").GetString()
            ?? throw new CliException("The new version answered without an uploadUrl.");

        // Used EXACTLY as returned, with no bearer token: it is signed, and points at object storage on a
        // possibly different host than the API (PublicServiceUrl, ADR 0213).
        await SimplArchiveApi.UploadAsync(new Uri(uploadUrl), licence, cancellationToken);

        // Confirm by following the new version's OWN `self`, never by appending the id to the collection
        // address — that is ADR 0557's "composing in disguise", and the first draft of this file did it. The
        // rel is advertised as GET and the confirm is a PUT to the same address, which is ADR 0719 working
        // as designed: one rel per resource, the METHOD says which action.
        //
        // Until this lands the version is Pending and the document has no current version — precisely what
        // activation then refuses as "no confirmed content version", so a failure here is worth telling
        // apart from a failed upload.
        var versionHref = Hypermedia.Href(Links(version), "self", $"The new version {versionId:D}");
        await api.PutAsync(versionHref, new { }, cancellationToken);

        return documentId;
    }

    /// <summary>
    /// Turns a document id back into its resource, through the root's templated <c>document</c> rel.
    /// </summary>
    /// <remarks>
    /// This is the id-to-resource turn ADR 0557 sanctions: a caller holding only an id must read once before
    /// it can follow anything. The address is the server's own template with the id substituted — the one
    /// place this tool may put a value into an advertised href, and it is why <c>--parent</c> takes an id at
    /// all rather than pretending a CLI argument can be a hypermedia link.
    /// </remarks>
    private static async Task<JsonElement> ResolveAsync(
        SimplArchiveApi api, Hypermedia hypermedia, Guid documentId, CancellationToken cancellationToken)
    {
        var template = await hypermedia.RootHrefAsync("document", cancellationToken);

        return await api.GetAsync(template.Replace("{id}", documentId.ToString("D"), StringComparison.Ordinal), cancellationToken);
    }

    private static Dictionary<string, string> Links(JsonElement resource)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);

        if (resource.TryGetProperty("links", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in array.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && link.TryGetProperty("href", out var href)
                    && rel.GetString() is { } name && href.GetString() is { } address)
                {
                    links.TryAdd(name, address);
                }
            }
        }

        return links;
    }
}

/// <summary>The messages this command writes, kept out of the flow so the flow reads as steps.</summary>
internal static class Describe
{
    internal static string DefaultName(LicenceClaims claims) =>
        $"Licence — {string.Join(", ", claims.ModuleIds)} — through {claims.SupportContractEnd}";

    internal static string NothingToActivate(LicenceClaims claims, ModuleTargets targets) =>
        targets.Licensable.Count == 0
            // NO module advertised a `license` rel — which is a different fact from "no modules", and the
            // distinction is the whole message. ADR 0543 makes a missing rel mean "not available to you,
            // here, now", and the one thing that withholds this entire surface is not being a tenant
            // administrator. Branching on modules being PRESENT instead got this wrong, and a test caught it.
            ? "This installation advertised no module you may license"
              + (targets.Installed.Count == 0
                  ? ", and listed none at all. Nothing filed."
                  : $" — it carries {string.Join(", ", targets.Installed)}, but offered no activation address "
                    + "for any of them. The modules surface is tenant-administrator only, so the signed-in "
                    + "user is probably not one. Nothing filed.")
            : $"Nothing to activate. The licence entitles {string.Join(", ", claims.ModuleIds)}; this "
              + $"installation can license {string.Join(", ", targets.Licensable)}. Nothing filed.";
}
