using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>One addressbook or calendar the caller can see (#564), as the Contacts/Calendar tabs need it.</summary>
/// <param name="Id">The typed folder.</param>
/// <param name="DisplayName">Parent-qualified, so two same-named collections are tellable apart (ADR 0619).</param>
/// <param name="Kind"><c>addressbook</c> or <c>calendar</c>.</param>
/// <param name="Color">The caller's effective colour — their override if set, else the collection's own.</param>
/// <param name="Writable">False ⇒ the tab shows the collection but disables its editors.</param>
/// <param name="IsPersonalDefault">The caller's own My Addressbook / My Calendar, listed first.</param>
/// <param name="CanCreateEntries">
/// Whether this caller may add an entry — what New is gated on. NOT the presence of the typed rel, which now
/// serves the LISTING too and is therefore advertised to any reader: gating on it would light New up for
/// someone who cannot create and fail with a 403 on click. Not <paramref name="Writable"/> either, which
/// reports the right to edit CONTENT and answers a different question again.
/// </param>
/// <param name="Links">Its advertised addresses; the tab follows these and composes nothing (ADR 0543).</param>
public sealed record DavCollection(
    Guid Id, string DisplayName, string Name, string Kind, string? Color, bool Writable, bool IsPersonalDefault,
    bool CanCreateEntries, IReadOnlyDictionary<string, string> Links)
{
    public string Href(string rel) => Links.TryGetValue(rel, out var href)
        ? href
        : throw new ApiActionException($"This collection does not offer '{rel}'.");

    /// <summary>
    /// The address for <paramref name="rel"/>, or null when the collection does not advertise it — which means
    /// "not available to you, here, now" (ADR 0543) and is what an affordance is gated on, not an error.
    /// </summary>
    public string? HrefOrNull(string rel) => Links.GetValueOrDefault(rel);
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
            c.TryGetProperty("canCreateEntries", out var cc) && cc.GetBoolean(),
            ApiCore.ParseLinks(c) ?? new Dictionary<string, string>())).ToList();
    }

    /// <summary>
    /// The entries filed in one collection, with their index fields — a whole tab's worth of rows in one
    /// request.
    /// </summary>
    /// <remarks>
    /// Followed from the collection's own typed rel, which serves both the listing (GET) and the create
    /// (POST). Before this the tabs built rows from the children listing, which carries a name and nothing
    /// else, so When, Where, Organization, Email and Phone were all empty strings and the detail pane beside
    /// them was blank by construction (#660).
    /// </remarks>
    public async Task<IReadOnlyList<DavEntry>> ListEntriesAsync(string href, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        var array = response.TryGetProperty("appointments", out var a) && a.ValueKind == JsonValueKind.Array
            ? a
            : response.TryGetProperty("contacts", out var c) && c.ValueKind == JsonValueKind.Array ? c : default;

        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        return array.EnumerateArray().Select(e => new DavEntry(
            e.GetProperty("id").GetGuid(),
            Text(e, "name") ?? string.Empty,
            Text(e, "start"),
            Text(e, "end"),
            Text(e, "location"),
            e.TryGetProperty("allDay", out var ad) && ad.GetBoolean(),
            Text(e, "fullName"),
            Text(e, "email"),
            Text(e, "phone"),
            Text(e, "organization"),
            ApiCore.ParseLinks(e) ?? new Dictionary<string, string>(),
            Text(e, "repeats"))).ToList();
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

/// <summary>
/// One listed contact or appointment, with the index fields its row and detail pane show.
/// </summary>
/// <remarks>
/// One shape for both, because the two tabs differ in which fields they display rather than in how a row is
/// fetched, addressed or selected. <paramref name="Start"/> is a STRING plus <paramref name="AllDay"/> rather
/// than a <c>DateTimeOffset</c>: a day and a moment are different things, and one typed field would have to
/// invent midnight for the all-day case — the inference ADR 0647 refuses.
/// </remarks>
public sealed record DavEntry(
    Guid Id, string Name, string? Start, string? End, string? Location, bool AllDay,
    string? FullName, string? Email, string? Phone, string? Organization,
    IReadOnlyDictionary<string, string> Links,
    string? Repeats = null)
{
    /// <summary>Whether this entry repeats — all a client needs, since the rule is never expanded here.</summary>
    public bool Recurring => !string.IsNullOrEmpty(Repeats);

    public string? HrefOrNull(string rel) => Links.GetValueOrDefault(rel);

    /// <summary>The start as an instant, or null when it is a day or absent — what a list orders by.</summary>
    public DateTimeOffset? StartsAt =>
        !AllDay && DateTimeOffset.TryParse(Start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;

    /// <summary>The end as an instant, or null for an all-day or undated entry.</summary>
    public DateTimeOffset? EndsAt =>
        !AllDay && DateTimeOffset.TryParse(End, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;

    /// <summary>The day this entry falls on, for both shapes — the key a month grid buckets by.</summary>
    public DateOnly? Day =>
        DateOnly.TryParse(Start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : StartsAt is { } at ? DateOnly.FromDateTime(at.LocalDateTime) : null;

    /// <summary>
    /// The day named by END, for both shapes — the raw <c>DTEND</c>, which iCalendar defines as EXCLUSIVE.
    /// </summary>
    /// <remarks>See the web twin: the exclusive-to-last-day conversion belongs where both shapes are decided
    /// together, not here, or this property's name would disagree with its value.</remarks>
    public DateOnly? EndDay =>
        DateOnly.TryParse(End, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : EndsAt is { } at ? DateOnly.FromDateTime(at.LocalDateTime) : null;
}
