using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Calendar tab's detail pane reads the selected appointment itself (ADR 0690): the zones each endpoint was
// recorded in, the link and the notes are not index fields, so the row cannot carry them.
//
// Driven against a real appointment with TWO different zones, because that is the case the pane exists for and
// the one every earlier version got wrong by construction — one zone field cannot express a flight.
[Collection(UiCollection.Name)]
public class DesktopAppointmentPreviewTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAppointmentPreviewTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_an_appointment_fills_the_pane_with_its_zones_link_and_notes()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);

        var summary = await SeedAsync(_app.BaseUrl, token);

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await vm.CalendarTab.LoadAsync();
        await WaitForAsync(() => vm.CalendarTab.Appointments.Any(a => a.Title == summary));

        vm.CalendarTab.Selected = vm.CalendarTab.Appointments.First(a => a.Title == summary);
        await WaitForAsync(() => vm.CalendarTab.Detail is not null);

        var detail = vm.CalendarTab.Detail;
        Assert.NotNull(detail);

        // Two recorded lines, because the two endpoints name different zones — one line would have to name a
        // zone that is wrong for half of it.
        Assert.Equal(2, detail!.RecordedLines.Count);
        Assert.Equal("Europe/Zurich", detail.RecordedLines[0].Zone);
        Assert.Equal("America/New_York", detail.RecordedLines[1].Zone);

        // UTC is always there: it is what this appointment can be compared against.
        Assert.Contains("07:00", detail.UtcRange);
        Assert.Contains("15:30", detail.UtcRange);

        // And the things no index field carries.
        Assert.Equal("Gate A42", detail.Location);
        Assert.Equal("https://airline.example.test/lx54", detail.Url);
        Assert.Equal("Seat 14A.", detail.Notes);
    }

    [Fact]
    public async Task Changing_the_selection_clears_the_previous_appointments_detail()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Its OWN appointment: the demo admin's calendar starts empty, so a test that picked "the first row"
        // would pass or fail on whether a sibling test had run first.
        var summary = await SeedAsync(_app.BaseUrl, await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await vm.CalendarTab.LoadAsync();
        await WaitForAsync(() => vm.CalendarTab.Appointments.Any(a => a.Title == summary));

        vm.CalendarTab.Selected = vm.CalendarTab.Appointments.First(a => a.Title == summary);
        await WaitForAsync(() => vm.CalendarTab.Detail is not null);

        // Deselecting must empty the pane SYNCHRONOUSLY. Leaving the previous appointment's notes up while the
        // next read is in flight is a claim about the wrong object, and it is invisible precisely because the
        // pane looks populated and correct (ADR 0559).
        vm.CalendarTab.Selected = null;
        Assert.Null(vm.CalendarTab.Detail);
    }

    /// <summary>Files a two-zone appointment in the caller's own calendar and answers its summary.</summary>
    private static async Task<string> SeedAsync(string baseUrl, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var summary = $"LX{Guid.NewGuid():N}"[..10];
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

        return summary;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
