using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>One addressbook or calendar the caller can see (#564), as the Contacts/Calendar tabs need it.</summary>
/// <param name="Id">The typed folder.</param>
/// <param name="DisplayName">Parent-qualified, so two same-named collections are tellable apart (ADR 0619).</param>
/// <param name="Kind"><c>addressbook</c> or <c>calendar</c>.</param>
/// <param name="Color">The caller's effective colour — their override if set, else the collection's own.</param>
/// <param name="Writable">False ⇒ the tab shows the collection but disables its editors.</param>
/// <param name="IsPersonalDefault">The caller's own My Contacts / My Calendar, listed first.</param>
/// <param name="Links">Its advertised addresses; the tab follows these and composes nothing (ADR 0543).</param>
public sealed record DavCollection(
    Guid Id, string DisplayName, string Name, string Kind, string? Color, bool Writable, bool IsPersonalDefault,
    IReadOnlyDictionary<string, string> Links)
{
    public string Href(string rel) => Links.TryGetValue(rel, out var href)
        ? href
        : throw new ApiActionException($"This collection does not offer '{rel}'.");
}

/// <summary>
/// Reads the caller's addressbooks and calendars from the `davCollections` rel on the me resource (#564).
/// The CalDAV/CardDAV home set answers the same question for EXTERNAL clients; ours speaks JSON and follows
/// rels, so it uses this rather than parsing a multistatus to draw a tab.
/// </summary>
public sealed class DavCollectionsClient
{
    private readonly ApiCore _core;
    private readonly ProfileClient _profile;

    public DavCollectionsClient(ApiCore core, ProfileClient profile)
    {
        _core = core;
        _profile = profile;
    }

    /// <summary>Every visible collection, personal defaults first. <paramref name="kind"/> narrows it.</summary>
    public async Task<IReadOnlyList<DavCollection>> ListAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        var href = await _profile.MeHrefAsync("davCollections", cancellationToken);

        // A query on an ADVERTISED href is following it, not composing one: the server owns the path, the
        // client owns the filter (ADR 0557).
        if (kind is { Length: > 0 })
        {
            href += (href.Contains('?') ? "&" : "?") + "kind=" + Uri.EscapeDataString(kind);
        }

        var response = await _core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        if (!response.TryGetProperty("collections", out var collections) || collections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return collections.EnumerateArray().Select(c => new DavCollection(
            c.GetProperty("id").GetGuid(),
            c.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
            c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            c.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
            c.TryGetProperty("color", out var col) && col.ValueKind is JsonValueKind.String ? col.GetString() : null,
            c.TryGetProperty("writable", out var w) && w.GetBoolean(),
            c.TryGetProperty("isPersonalDefault", out var p) && p.GetBoolean(),
            ApiCore.ParseLinks(c) ?? new Dictionary<string, string>())).ToList();
    }

    /// <summary>Sets the caller's personal colour for a collection; null resets it to the collection's own.</summary>
    public async Task SetColorAsync(DavCollection collection, string? color, CancellationToken cancellationToken = default)
    {
        var href = collection.Href("collection-color");
        using var response = color is { Length: > 0 }
            ? await _core.Http.PutAsJsonAsync(href, new { color }, cancellationToken)
            : await _core.Http.DeleteAsync(href, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
