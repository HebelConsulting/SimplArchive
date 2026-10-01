using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Calendar tab's detail pane, on the web (ADR 0690, and ADR 0511 keeps the pair one surface): one instant
// read three ways — UTC, as the organiser recorded it, and in the reader's own zone when that says something
// new — plus the link and the notes, none of which are index fields.
//
// The appointment carries TWO different zones on purpose: it is the case the pane exists for, and the one a
// single zone field cannot express.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebAppointmentPreviewTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAppointmentPreviewTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_pane_reads_the_appointment_in_three_zones_with_its_link_and_notes()
    {
        var summary = $"LX{Guid.NewGuid():N}"[..10];

        // IN THE MONTH THE CALENDAR OPENS ON (#1516). This was a hardcoded 2026-09-01, and the tab opens on
        // the CURRENT month, fetching it ±7 days — so the test passed every day of September 2026 and began
        // failing at midnight on 1 October, on EVERY run, for as long as the fixed month stayed in the past.
        // A date-expiring test is not a flake: it fails deterministically from a date nobody wrote down, and
        // the first run after midnight points at whatever merged last.
        //
        // The 15th, which no DST transition can land on: the EU switches on the last Sunday of March and
        // October (the 25th at the earliest) and the US on the second Sunday of March (the 14th at the
        // latest) and the first Sunday of November. So neither conversion below is ambiguous or invalid.
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(15 - DateTime.Today.Day);

        // AND THE EXPECTED READINGS ARE DERIVED, not written out — which is the half that makes the anchor
        // safe. The clock faces the pane shows depend on each zone's offset ON THAT DATE: 09:00 in Zurich is
        // 07:00 UTC in summer and 08:00 in winter. Anchoring the date while keeping literal readings would
        // simply move the expiry to the next DST change, where it would fail twice a year and read as a
        // flake. Computed from the IANA database rather than from the server's own conversion, so this stays
        // an independent expectation rather than a copy of the code under test.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            day.ToDateTime(new TimeOnly(9, 0)), TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(
            day.ToDateTime(new TimeOnly(11, 30)), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var personal = await (await http.PostAsJsonAsync("/api/me/personal-repository", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var calendarId = (await http.GetFromJsonAsync<JsonElement>(
                $"/api/documents/{personal.GetProperty("id").GetGuid()}/children?limit=200"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Calendar")
            .GetProperty("id").GetGuid();

        (await http.PostAsJsonAsync($"/api/documents/{calendarId}/appointments", new
        {
            summary,
            start = $"{day:yyyy-MM-dd}T09:00:00",
            end = $"{day:yyyy-MM-dd}T11:30:00",
            isAllDay = false,
            startTimeZoneId = "Europe/Zurich",
            endTimeZoneId = "America/New_York",
            location = "Gate A42",
            url = "https://airline.example.test/lx54",
            description = "Seat 14A.",
        })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        // By the tab's aria-label, not its text: the bottom bar hides labels where a hover can explain the
        // icon (ADR 0576), so the span exists and is not visible.
        await page.Locator(".wb-tab[aria-label='Calendar']").First.ClickAsync();

        var tab = page.Locator(".wb-calendar");
        await Expect(tab.GetByText(summary).First).ToBeVisibleAsync();
        await tab.GetByText(summary).First.ClickAsync();

        // The three readings. UTC first, and the two endpoints are hours apart in a way a single zone field
        // could never produce, which is the whole case this pane exists for.
        //
        // The old comment here said "eight and a half hours", and that is not invariant: Zurich switches on
        // the last Sunday of October and New York on the first Sunday of November, so for the week between
        // them the same two wall-clock times are 7.5 hours apart (08:00Z to 15:30Z). Measured, not reasoned
        // about — which is why the expectation is derived per date rather than stated as a constant.
        //
        // Matched over both clock conventions: the times are formatted in the BROWSER's culture, so the same
        // instant reads "07:00" or "7:00 AM" depending on the machine running the suite. Pinning one spelling
        // makes the test a report about the runner's locale.
        await Expect(tab.GetByText(ClockFace(startUtc)).First).ToBeVisibleAsync();
        await Expect(tab.GetByText(ClockFace(endUtc)).First).ToBeVisibleAsync();

        // As recorded, with the zone each endpoint names.
        await Expect(tab.GetByText("Europe/Zurich").First).ToBeVisibleAsync();
        await Expect(tab.GetByText("America/New_York").First).ToBeVisibleAsync();

        // And what no index field carries.
        await Expect(tab.GetByText("Gate A42").First).ToBeVisibleAsync();
        await Expect(tab.GetByText("Seat 14A.").First).ToBeVisibleAsync();
        await Expect(tab.GetByRole(AriaRole.Link, new() { Name = "https://airline.example.test/lx54" }))
            .ToBeVisibleAsync();
    }

    /// <summary>One instant, in either clock convention the browser's culture might render it.</summary>
    /// <remarks>
    /// Built from the time rather than written out, so the assertion follows the derived expectation above
    /// instead of restating it — a literal here would re-introduce exactly the coupling to a particular month
    /// that #1516 is about. Midnight renders as 12 AM rather than 0 AM, hence the modulo.
    /// </remarks>
    private static System.Text.RegularExpressions.Regex ClockFace(DateTime utc) =>
        new($@"\b0?{utc.Hour}:{utc.Minute:00}\b|\b{(utc.Hour % 12 == 0 ? 12 : utc.Hour % 12)}:{utc.Minute:00}\s*[AP]M");
}
