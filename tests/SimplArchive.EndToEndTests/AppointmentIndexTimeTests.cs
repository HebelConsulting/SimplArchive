using System.Globalization;

namespace SimplArchive.EndToEndTests;

// The Appointment mask's Start/End used to be `Date` — a concert at 19:00 and one at 21:00 indexed identically
// (#660). A list built from index data therefore could not show a time or order two shows on one day, which is
// exactly what the Calendar tab needs and what 67 seeded concerts made obvious.
//
// They are now `DateTime`, stored ISO-8601 WITH AN OFFSET: the entry's own zone where it has one, the server's
// where it floats. The projection gains a comparable instant; the stored .ics keeps its floating time, so DAV
// clients round-trip unchanged (ADR 0631/0647).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AppointmentIndexTimeTests
{
    private readonly E2EApiFactory _factory;

    public AppointmentIndexTimeTests(E2EApiFactory factory) => _factory = factory;

    private static string Href(System.Text.Json.JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private async Task<(HttpClient Api, Guid CalendarId)> CalendarAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"idxtime-{Guid.NewGuid():N}@e2e.local";
        const string password = "idxtime-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Index Time");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var calendarId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Calendar").GetProperty("id").GetGuid();
        return (api, calendarId);
    }

    private static async Task<string?> IndexValueAsync(HttpClient api, Guid documentId, string field) =>
        (await TestJson.Get(api, $"/api/documents/{documentId}/index-data"))
            .GetProperty("fields").EnumerateArray()
            .FirstOrDefault(f => f.GetProperty("fieldName").GetString() == field)
            .GetProperty("values").EnumerateArray().FirstOrDefault().GetString();

    [Fact]
    public async Task A_timed_appointment_indexes_its_time_with_an_offset()
    {
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        var start = new DateTime(2026, 8, 29, 19, 0, 0, DateTimeKind.Unspecified);
        var created = await TestJson.Post(api, Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments"), new
        {
            summary = "The Iron Horse",
            start,
            end = start.AddHours(2),
        });

        var indexed = await IndexValueAsync(api, created.GetProperty("id").GetGuid(), "Start");
        Assert.NotNull(indexed);

        // The hour survives — this is the whole point; a `Date` field silently dropped it.
        Assert.Contains("19:00", indexed!, StringComparison.Ordinal);

        // …and it parses as a real instant, carrying an offset rather than being a bare wall clock. Asserted by
        // PARSING rather than by matching text, so it holds on a UTC container (+00:00) and on a machine in
        // Zurich (+02:00) alike — the value is environment-dependent by design, its SHAPE is not.
        Assert.True(
            DateTimeOffset.TryParse(indexed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment),
            $"'{indexed}' is not a parseable instant.");
        Assert.Equal(19, moment.Hour);
        Assert.Contains(indexed!.Contains('+', StringComparison.Ordinal) ? "+" : "-", indexed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_all_day_appointment_keeps_a_plain_date()
    {
        // A day is not a moment: it has no time to place in a zone, so stamping midnight on it would invent
        // one — the same inference the rule exists to avoid. It stays `yyyy-MM-dd`.
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        var day = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Unspecified);
        var created = await TestJson.Post(api, Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments"), new
        {
            summary = "Lluís Coloma Trio",
            start = day,
            end = day,
            isAllDay = true,
        });

        var indexed = await IndexValueAsync(api, created.GetProperty("id").GetGuid(), "Start");
        Assert.Equal("2026-08-21", indexed);
    }

    [Fact]
    public async Task Two_shows_on_one_day_are_orderable_by_their_indexed_start()
    {
        // The case a date-only field could not express at all, and the one the seed produces for real: the same
        // venue, the same day, an early and a late show.
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        var href = Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments");
        var day = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified);

        var early = (await TestJson.Post(api, href, new { summary = "Shalin Liu — early", start = day.AddHours(17), end = day.AddHours(19) }))
            .GetProperty("id").GetGuid();
        var late = (await TestJson.Post(api, href, new { summary = "Shalin Liu — late", start = day.AddHours(20), end = day.AddHours(22) }))
            .GetProperty("id").GetGuid();

        var earlyStart = DateTimeOffset.Parse((await IndexValueAsync(api, early, "Start"))!, CultureInfo.InvariantCulture);
        var lateStart = DateTimeOffset.Parse((await IndexValueAsync(api, late, "Start"))!, CultureInfo.InvariantCulture);

        Assert.True(earlyStart < lateStart, $"the 17:00 show ({earlyStart:o}) did not sort before the 20:00 one ({lateStart:o})");
    }

    // A recurrence set is never expanded, so a weekly rehearsal is drawn at its FIRST occurrence and nowhere
    // else. That limitation is deliberate; a SILENT one is a grid claiming the other three weeks are free. So
    // the rule is INDEXED — a listing can then say "this repeats" without opening one .ics per row, which is
    // the per-row cost ADR 0557 forbids.
    [Fact]
    public async Task A_repeating_appointment_indexes_its_rule_and_reports_it_on_the_listing()
    {
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        var href = Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments");
        var start = new DateTime(2026, 9, 1, 19, 0, 0, DateTimeKind.Unspecified);

        var repeating = (await TestJson.Post(api, href, new
        {
            summary = "Weekly rehearsal",
            start,
            end = start.AddHours(2),
            recurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
        })).GetProperty("id").GetGuid();

        var once = (await TestJson.Post(api, href, new { summary = "One-off", start = start.AddDays(1), end = start.AddDays(1).AddHours(1) }))
            .GetProperty("id").GetGuid();

        // Indexed from the STORED item, through the library's own writer — so what the index says and what the
        // .ics says cannot disagree about whether something repeats.
        var indexed = await IndexValueAsync(api, repeating, "Repeats");
        Assert.NotNull(indexed);
        Assert.Contains("FREQ=WEEKLY", indexed!, StringComparison.Ordinal);

        // And it reaches the listing, which is where a client reads it: one request for the whole tab.
        var entries = (await TestJson.Get(api, href)).GetProperty("appointments").EnumerateArray().ToList();

        var repeatingRow = entries.Single(e => e.GetProperty("id").GetGuid() == repeating);
        Assert.Contains("FREQ=WEEKLY", repeatingRow.GetProperty("repeats").GetString()!, StringComparison.Ordinal);

        // The negative case is the half that matters: a marker shown on everything says nothing at all.
        var onceRow = entries.Single(e => e.GetProperty("id").GetGuid() == once);
        Assert.True(
            onceRow.GetProperty("repeats").ValueKind is System.Text.Json.JsonValueKind.Null,
            $"a non-repeating entry reported repeats='{onceRow.GetProperty("repeats")}'.");
    }
}
