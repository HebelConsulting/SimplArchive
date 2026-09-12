using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// Editing a repeating entry: this occurrence / this and following / all (#1133).
//
// Both editors showed recurrence READ-ONLY until this existed, and said why: offering a rule box without the
// scope choice applies every edit to the whole series, including occurrences already past. These are the
// three operations that make the choice real.
//
// Each is ONE request. A client doing it in two — cancel the occurrence, then create its replacement — leaves
// the day cancelled with nothing in its place whenever the second call fails.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AppointmentEditScopeTests
{
    private readonly E2EApiFactory _factory;

    public AppointmentEditScopeTests(E2EApiFactory factory) => _factory = factory;

    private const string Weekdays = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;UNTIL=20261030T235959Z";

    private static DateTime Monday(int hour) => new(2026, 9, 14, hour, 0, 0, DateTimeKind.Utc);

    private async Task<(HttpClient Api, Guid CalendarId, Guid SeriesId)> SeriesAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"scope-{Guid.NewGuid():N}@e2e.local";
        const string password = "editscope-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Scopes", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var calendarId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Calendar").GetProperty("id").GetGuid();

        var created = await api.PostAsJsonAsync($"/api/documents/{calendarId}/appointments", new
        {
            summary = "Stand-up",
            start = Monday(9),
            end = Monday(10),
            recurrenceRule = Weekdays,
        });
        var seriesId = (await TestJson.Read(created)).GetProperty("id").GetGuid();

        return (api, calendarId, seriesId);
    }

    private static async Task<string> ETagAsync(HttpClient api, Guid documentId)
    {
        using var response = await api.GetAsync($"/api/documents/{documentId}/appointment");
        return response.Headers.ETag!.Tag;
    }

    private static async Task<List<System.Text.Json.JsonElement>> WeekAsync(HttpClient api, Guid calendarId) =>
        [.. (await TestJson.Get(api,
                $"/api/documents/{calendarId}/appointments?from=2026-09-14T00:00:00Z&to=2026-09-21T00:00:00Z"))
            .GetProperty("appointments").EnumerateArray()];

    /// <summary>
    /// The occurrence identity the LISTING gives for a day — which is what a client holds and sends back.
    /// </summary>
    /// <remarks>
    /// Never constructed by hand. A floating entry is stamped with the SERVER's zone at index time, so
    /// "Wednesday 09:00" as the user typed it is not 09:00Z — and a recurrenceId invented from the wall clock
    /// cancels a day that does not exist. The first version of this test did exactly that and reported the
    /// feature broken.
    /// </remarks>
    private static async Task<string> RecurrenceIdAsync(HttpClient api, Guid calendarId, Guid seriesId, int dayOfMonth) =>
        (await WeekAsync(api, calendarId))
            .Where(row => row.GetProperty("id").GetGuid() == seriesId)
            .Select(row => row.GetProperty("recurrenceId").GetString()!)
            .Single(id => DateTimeOffset.Parse(id).UtcDateTime.Day == dayOfMonth);

    private static async Task<System.Net.Http.HttpResponseMessage> EditAsync(
        HttpClient api, Guid seriesId, string etag, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{seriesId}/appointment")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await api.SendAsync(request);
    }

    // ONE occurrence moves: the Wednesday becomes an entry of its own, and the series keeps the other four
    // days at the time they were always held.
    [Fact]
    public async Task This_occurrence_cancels_the_day_and_files_the_edit_beside_it()
    {
        var (api, calendarId, seriesId) = await SeriesAsync();
        var wednesdayId = await RecurrenceIdAsync(api, calendarId, seriesId, 16);
        var wednesday = DateTimeOffset.Parse(wednesdayId);

        var response = await EditAsync(api, seriesId, await ETagAsync(api, seriesId), new
        {
            summary = "Stand-up (late)",
            start = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc),
            scope = "this",
            recurrenceId = wednesdayId,
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);

        var week = await WeekAsync(api, calendarId);

        // Still five entries that week — four from the series, one standing alone.
        Assert.Equal(5, week.Count);

        // The series no longer holds Wednesday 09:00...
        Assert.DoesNotContain(week, row =>
            row.GetProperty("id").GetGuid() == seriesId
            && row.GetProperty("recurrenceId").GetString() is { } id
            && DateTimeOffset.Parse(id) == wednesday);

        // ...and the moved one is a DIFFERENT document, at the new time, repeating nothing.
        var moved = Assert.Single(week, row => row.GetProperty("id").GetGuid() != seriesId);
        Assert.Equal("Stand-up (late)", moved.GetProperty("name").GetString());
        Assert.Null(moved.GetProperty("recurrenceId").GetString());

        api.Dispose();
    }

    // The split: everything already held keeps the values it was held with, which is the whole reason this
    // choice exists rather than just "all".
    [Fact]
    public async Task This_and_following_ends_the_series_and_starts_a_new_one()
    {
        var (api, calendarId, seriesId) = await SeriesAsync();
        var wednesdayId = await RecurrenceIdAsync(api, calendarId, seriesId, 16);
        var wednesday = DateTimeOffset.Parse(wednesdayId);

        var response = await EditAsync(api, seriesId, await ETagAsync(api, seriesId), new
        {
            summary = "Stand-up (new time)",
            start = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc),
            recurrenceRule = Weekdays,
            scope = "following",
            recurrenceId = wednesdayId,
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);

        var week = await WeekAsync(api, calendarId);

        // Monday and Tuesday stayed with the original series...
        var original = week.Where(row => row.GetProperty("id").GetGuid() == seriesId).ToList();
        Assert.Equal(2, original.Count);
        Assert.All(original, row =>
            Assert.True(DateTimeOffset.Parse(row.GetProperty("recurrenceId").GetString()!) < wednesday));

        // ...and Wednesday onwards belongs to a new one, at the new time.
        var carried = week.Where(row => row.GetProperty("id").GetGuid() != seriesId).ToList();
        Assert.Equal(3, carried.Count);
        Assert.All(carried, row => Assert.Equal("Stand-up (new time)", row.GetProperty("name").GetString()));

        api.Dispose();
    }

    // The default, and what a PUT of a series has always meant — so a client that never heard of the scope
    // keeps working exactly as before.
    [Fact]
    public async Task All_rewrites_the_whole_series()
    {
        var (api, calendarId, seriesId) = await SeriesAsync();

        var response = await EditAsync(api, seriesId, await ETagAsync(api, seriesId), new
        {
            summary = "Stand-up (renamed)",
            start = Monday(9),
            end = Monday(10),
            recurrenceRule = Weekdays,
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);

        var week = await WeekAsync(api, calendarId);

        Assert.Equal(5, week.Count);
        Assert.All(week, row =>
        {
            Assert.Equal(seriesId, row.GetProperty("id").GetGuid());
            Assert.Equal("Stand-up (renamed)", row.GetProperty("name").GetString());
        });

        api.Dispose();
    }

    // A scope aimed at an entry that does not repeat is a caller mistake worth SAYING, not a silent rewrite of
    // the one entry: the client asked for something that cannot mean what it thinks.
    [Fact]
    public async Task A_scope_on_a_one_off_entry_is_refused()
    {
        var (api, calendarId, _) = await SeriesAsync();

        var created = await api.PostAsJsonAsync($"/api/documents/{calendarId}/appointments", new
        {
            summary = "One-off",
            start = new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc),
        });
        var oneOffId = (await TestJson.Read(created)).GetProperty("id").GetGuid();

        var response = await EditAsync(api, oneOffId, await ETagAsync(api, oneOffId), new
        {
            summary = "One-off (edited)",
            start = new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc),
            scope = "this",
            recurrenceId = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero).ToString("O"),
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        api.Dispose();
    }
}
