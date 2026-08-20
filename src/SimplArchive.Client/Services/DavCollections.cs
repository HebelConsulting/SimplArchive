using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SimplArchive.Client.Services;

/// <summary>One addressbook or calendar, as the client tabs need it (#564).</summary>
/// <remarks>
/// The CalDAV/CardDAV home set answers the same question for EXTERNAL clients; ours speaks JSON and follows
/// rels, so it reads <c>/api/dav-collections</c> instead of parsing a multistatus to draw a tab.
/// </remarks>
public sealed class DavCollection
{
    public Guid Id { get; set; }

    /// <summary>Parent-qualified, so two same-named collections are tellable apart (ADR 0619).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary><c>addressbook</c> or <c>calendar</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? Color { get; set; }

    public bool Writable { get; set; }

    /// <summary>
    /// Whether this caller may add an entry here — what New is gated on.
    /// </summary>
    /// <remarks>
    /// Not the presence of the typed rel, which now serves the LISTING too and is therefore advertised to any
    /// reader: gating on it would light New up for someone who cannot create and fail with a 403 on click.
    /// Not <c>Writable</c> either, which reports the right to edit content and answers a different question.
    /// </remarks>
    public bool CanCreateEntries { get; set; }

    public bool IsPersonalDefault { get; set; }

    public List<LinkDto> Links { get; set; } = [];

    /// <summary>The advertised href for <paramref name="rel"/>, or null when the server did not offer it.</summary>
    /// <remarks>
    /// Null rather than a composed fallback: a rel the server did not advertise means the action is not
    /// available here (ADR 0543), and rebuilding the URL would paper over exactly what that rule prevents.
    /// </remarks>
    public string? Href(string rel) =>
        Links.FirstOrDefault(l => string.Equals(l.Rel, rel, StringComparison.Ordinal))?.Href;

    public sealed class LinkDto
    {
        public string Rel { get; set; } = string.Empty;

        public string Href { get; set; } = string.Empty;
    }
}

/// <summary>Reads the caller's addressbooks and calendars, following the <c>davCollections</c> me-rel.</summary>
public sealed class DavCollections
{
    private readonly HttpClient _http;
    private readonly ApiRoot _apiRoot;

    public DavCollections(HttpClient http, ApiRoot apiRoot)
    {
        _http = http;
        _apiRoot = apiRoot;
    }

    /// <param name="kind"><c>addressbook</c> or <c>calendar</c>.</param>
    /// <remarks>
    /// The kind rides as a QUERY on the advertised href, which is following it rather than composing one: the
    /// server owns the path, the caller owns the filter (ADR 0557). Appending a path SEGMENT would not be.
    /// </remarks>
    public async Task<List<DavCollection>> ListAsync(string kind, CancellationToken cancellationToken = default)
    {
        var href = await _apiRoot.MeHrefAsync("davCollections", cancellationToken);
        if (href is null)
        {
            // The rel is absent for a principal with no personal space — a ServiceAccount, say. An empty list
            // is the honest answer; the tab then shows its "nothing selected" line rather than an error.
            return [];
        }

        var response = await _http.GetFromJsonAsync<ListResponse>(
            $"{href}?kind={Uri.EscapeDataString(kind)}", cancellationToken);
        return response?.Collections ?? [];
    }

    /// <summary>
    /// The entries filed in one collection, with their index fields — one request for a whole tab's worth of
    /// rows.
    /// </summary>
    /// <remarks>
    /// Followed from the collection's own typed rel (<c>contacts</c>/<c>appointments</c>), which serves both
    /// the listing and the create. Before this the tabs built rows from the children listing, which carries a
    /// name and nothing else — so When, Where, e-mail and phone were rendered from empty strings and the
    /// detail pane beside them was blank by construction (#660).
    /// </remarks>
    public async Task<List<DavEntry>> ListEntriesAsync(string href, CancellationToken cancellationToken = default)
    {
        var response = await _http.GetFromJsonAsync<EntriesResponse>(href, cancellationToken);
        return response?.Appointments.Count > 0 ? response.Appointments : response?.Contacts ?? [];
    }

    private sealed class ListResponse
    {
        [JsonPropertyName("collections")]
        public List<DavCollection> Collections { get; set; } = [];
    }

    private sealed class EntriesResponse
    {
        [JsonPropertyName("appointments")]
        public List<DavEntry> Appointments { get; set; } = [];

        [JsonPropertyName("contacts")]
        public List<DavEntry> Contacts { get; set; } = [];
    }
}

/// <summary>
/// One listed contact or appointment. One shape for both, because the two tabs differ in which fields they
/// show rather than in how a row is fetched, addressed or selected.
/// </summary>
public sealed class DavEntry
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    // Appointment
    /// <summary>ISO-8601 with an offset for a timed entry, <c>yyyy-MM-dd</c> for an all-day one.</summary>
    public string? Start { get; set; }

    public string? End { get; set; }

    public string? Location { get; set; }

    /// <summary>A day rather than a moment — <see cref="Start"/> carries no time (ADR 0647).</summary>
    public bool AllDay { get; set; }

    /// <summary>The stored <c>RRULE</c> as text, or null when the entry does not repeat.</summary>
    public string? Repeats { get; set; }

    /// <summary>Whether this entry repeats — all a client needs, since the rule is never expanded here.</summary>
    public bool Recurring => !string.IsNullOrEmpty(Repeats);

    // Contact
    public string? FullName { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Organization { get; set; }

    public List<DavCollection.LinkDto> Links { get; set; } = [];

    public string? Href(string rel) =>
        Links.FirstOrDefault(l => string.Equals(l.Rel, rel, StringComparison.Ordinal))?.Href;

    /// <summary>
    /// The start as an instant, or null when it is a day or absent — what a list orders by and a grid places.
    /// </summary>
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
    /// <remarks>
    /// Deliberately NOT adjusted here. The classifier stamps <c>DTEND</c> verbatim, so this is the day the entry
    /// stops rather than its last day, and turning one into the other is the grid's job (<c>CoversDay</c>) where
    /// the all-day and timed shapes are decided together. Adjusting it here would leave a property whose name
    /// says "end" and whose value is a day earlier than the stored one.
    /// </remarks>
    public DateOnly? EndDay =>
        DateOnly.TryParse(End, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : EndsAt is { } at ? DateOnly.FromDateTime(at.LocalDateTime) : null;
}
