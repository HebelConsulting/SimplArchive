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
            start = "2026-09-01T09:00:00",
            end = "2026-09-01T11:30:00",
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

        // The three readings. UTC first: 09:00 in Zurich is 07:00 UTC, and 11:30 in New York is 15:30 —
        // eight and a half hours, which is the number a single zone field could never produce.
        //
        // Matched with a regex over both clock conventions: the times are formatted in the BROWSER's culture,
        // so the same instant reads "07:00 – 15:30" or "7:00 AM – 3:30 PM" depending on the machine running
        // the suite. Pinning one spelling makes the test a report about the runner's locale.
        await Expect(tab.GetByText(new System.Text.RegularExpressions.Regex(@"\b0?7:00")).First).ToBeVisibleAsync();
        await Expect(tab.GetByText(new System.Text.RegularExpressions.Regex(@"(15:30|3:30 PM)")).First).ToBeVisibleAsync();

        // As recorded, with the zone each endpoint names.
        await Expect(tab.GetByText("Europe/Zurich").First).ToBeVisibleAsync();
        await Expect(tab.GetByText("America/New_York").First).ToBeVisibleAsync();

        // And what no index field carries.
        await Expect(tab.GetByText("Gate A42").First).ToBeVisibleAsync();
        await Expect(tab.GetByText("Seat 14A.").First).ToBeVisibleAsync();
        await Expect(tab.GetByRole(AriaRole.Link, new() { Name = "https://airline.example.test/lx54" }))
            .ToBeVisibleAsync();
    }
}
