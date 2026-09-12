using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// A repeating entry is listed once per OCCURRENCE when the caller asks for a window (#1133).
//
// The scope choice an editor needs — this occurrence / this and following / all — has nothing to act on
// while a series is a single row: the month grid showed the first occurrence with a "repeats" marker, and
// its own comment said that marker was the only thing admitting the grid showed less than the month held.
//
// Expanded on the SERVER so there is one implementation. Both clients render what they are given, and a
// surface added later inherits the same answer rather than reimplementing RRULE arithmetic.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AppointmentOccurrenceListingTests
{
    private readonly E2EApiFactory _factory;

    public AppointmentOccurrenceListingTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Guid CalendarId)> CalendarAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"occ-{Guid.NewGuid():N}@e2e.local";
        const string password = "occurrence-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Occurrences", canManageRepositories: true, isTenantAdmin: true);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // The caller's own My Calendar, which provisioning creates — the same way the other appointment tests
        // get one, rather than composing a typed folder by hand.
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var calendarId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Calendar").GetProperty("id").GetGuid();

        return (api, calendarId);
    }

    [Fact]
    public async Task A_weekday_series_is_listed_once_per_working_day()
    {
        var (api, calendarId) = await CalendarAsync();

        // Monday 14 September 2026, 09:00–10:00, every weekday.
        await api.PostAsJsonAsync($"/api/documents/{calendarId}/appointments", new
        {
            summary = "Stand-up",
            start = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc),
            recurrenceRule = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;UNTIL=20261030T235959Z",
        });

        // Without a window the listing answers as it always has: ONE row for the series, which is what CalDAV
        // and every other caller still wants.
        var series = (await TestJson.Get(api, $"/api/documents/{calendarId}/appointments"))
            .GetProperty("appointments").EnumerateArray().ToList();
        Assert.Single(series);
        Assert.Null(series[0].GetProperty("recurrenceId").GetString());

        // With one, the same series comes back per occurrence — five in the week beginning Monday, the
        // weekend skipped, which is the whole point of BYDAY over a daily rule.
        var week = (await TestJson.Get(api,
                $"/api/documents/{calendarId}/appointments?from=2026-09-14T00:00:00Z&to=2026-09-21T00:00:00Z"))
            .GetProperty("appointments").EnumerateArray().ToList();

        Assert.Equal(5, week.Count);
        Assert.All(week, row => Assert.Equal(series[0].GetProperty("id").GetGuid(), row.GetProperty("id").GetGuid()));

        // Each carries the instant that IDENTIFIES it — the same value an EXDATE carries, and what lets an
        // editor act on "this occurrence" rather than only on the whole series.
        var days = week
            .Select(row => DateTimeOffset.Parse(row.GetProperty("recurrenceId").GetString()!).DayOfWeek)
            .ToList();
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            days);

        api.Dispose();
    }

    // An entry that does not repeat passes through untouched — the common case, and the one every caller had
    // before any of this existed.
    [Fact]
    public async Task A_one_off_entry_is_listed_once_either_way()
    {
        var (api, calendarId) = await CalendarAsync();

        await api.PostAsJsonAsync($"/api/documents/{calendarId}/appointments", new
        {
            summary = "Review",
            start = new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc),
            end = new DateTime(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc),
        });

        var windowed = (await TestJson.Get(api,
                $"/api/documents/{calendarId}/appointments?from=2026-09-14T00:00:00Z&to=2026-09-21T00:00:00Z"))
            .GetProperty("appointments").EnumerateArray().ToList();

        Assert.Single(windowed);
        Assert.Null(windowed[0].GetProperty("recurrenceId").GetString());

        api.Dispose();
    }
}
