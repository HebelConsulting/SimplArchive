using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop Calendar tab (#564), the twin of Contacts — same listing, same checkbox model, ordered by time.
// The pair is one surface (ADR 0511), so what is asserted of one is asserted of the other.
[Collection(UiCollection.Name)]
public class DesktopCalendarTabTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCalendarTabTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Tab_opens_on_the_personal_calendar_and_lists_only_calendars()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var vm = new CalendarTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();

        var mine = Assert.Single(vm.Collections, c => c.Collection.IsPersonalDefault);
        Assert.Equal("My Calendar", mine.Collection.Name);
        Assert.EndsWith("My Calendar", mine.DisplayName, StringComparison.Ordinal); // parent-qualified
        Assert.True(mine.Writable);
        Assert.True(mine.IsChecked);

        // Only calendars — the addressbooks answer the same shape and must not leak in.
        Assert.All(vm.Collections, c => Assert.Equal("calendar", c.Collection.Kind));
        Assert.DoesNotContain(vm.Collections, c => c.Collection.Name == "My Contacts");

        // Every collection carries the addresses the tab acts from, so it never composes one (ADR 0543).
        Assert.All(vm.Collections, c =>
        {
            Assert.NotNull(c.Collection.Href("children"));
            Assert.NotNull(c.Collection.Href("collection-color"));
        });

        mine.IsChecked = false;
        await vm.OnCollectionToggledAsync();
        Assert.Empty(vm.Appointments);
        Assert.False(vm.CanCreate);
    }

    // The same containment fix the Contacts tab exercised, on the calendar side: an .ics through the ORDINARY
    // upload path, not only through CalDAV.
    [Fact]
    public async Task An_ics_uploaded_into_a_calendar_becomes_an_appointment_the_tab_lists()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        Assert.NotNull(await api.Profile.GetPersonalRepositoryAsync());

        var calendar = (await api.DavCollections.ListAsync("calendar")).Single(c => c.IsPersonalDefault);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SimplArchive//desktop//EN\r\nBEGIN:VEVENT\r\n"
            + $"UID:{suffix}\r\nDTSTAMP:20260817T090000Z\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\n"
            + $"SUMMARY:Sprint planning\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await api.Documents.UploadFileAsync(calendar.Href("children"), $"evt-{suffix}.ics", Encoding.UTF8.GetBytes(ics));

        var vm = new CalendarTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();

        // Named by the event's SUMMARY, not by the file — that rename is the proof the classifier ran rather
        // than the .ics landing as a plain Basic Entry, which containment would then have refused.
        Assert.Contains(vm.Appointments, a => a.Title == "Sprint planning");
    }

    // A row's two time displays must agree. They are computed by different code — one formats the range, the
    // other the full start — and binding the raw DateTimeOffset formatted the STORED OFFSET while the range
    // converted to local, so one appointment read "When 11:00–12:00" and "Starts 09:00" two lines apart.
    [Fact]
    public void The_row_and_the_detail_pane_tell_the_same_time()
    {
        var vm = new CalendarTabViewModel();
        vm.PopulateDemoForScreenshot();

        var row = vm.Appointments.First(a => a.Start is not null);
        Assert.Contains(row.Start!.Value.LocalDateTime.ToString("HH:mm"), row.TimeRange, StringComparison.Ordinal);
        Assert.Contains(row.Start!.Value.LocalDateTime.ToString("HH:mm"), row.StartsOn, StringComparison.Ordinal);
    }

    // Undated appointments sort LAST: a null start ordering to the top would put the least informative rows
    // exactly where the eye lands.
    [Fact]
    public void An_undated_appointment_sorts_after_the_dated_ones()
    {
        var vm = new CalendarTabViewModel();
        vm.PopulateDemoForScreenshot();
        vm.Appointments.Add(new AppointmentRowViewModel
        {
            Id = Guid.NewGuid(),
            CollectionColor = "#8a8a8a",
            CollectionName = "Personal / My Calendar",
            Title = "No date at all",
            Links = new Dictionary<string, string>(),
        });

        var ordered = vm.Appointments
            .OrderBy(a => a.Start is null).ThenBy(a => a.Start)
            .Select(a => a.Title).ToList();
        Assert.Equal("No date at all", ordered[^1]);
    }
}
