using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The meeting-room booking surface (ADRs 0735/0743), driven through the REAL view-model against the real
// Api: the dialog lists, books, refuses a taken slot with the localized sentence, and cancels. VM-level on
// purpose — the suite's Chrome-free contract — and the same lifecycle the view drives (LoadAsync on open,
// commands from buttons).
[Collection(UiCollection.Name)]
public class DesktopBookingDialogTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopBookingDialogTests(SelfHostedAppFixture app) => _app = app;

    private async Task<SimplArchiveApiClient> ApiAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        return new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
    }

    private async Task<(SimplArchiveApiClient Api, string BookingsHref, string RoomName)> RoomAsync()
    {
        var api = await ApiAsync();
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");

        // The room is created the way the real client does it: from the node's ADMITS list — the New
        // menu's data (#678) — never from the assignability picker, which deliberately excludes folder
        // masks (#580: the picker offers only what a document may WEAR).
        var admits = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository").Admits
            ?? throw new InvalidOperationException("The repository advertised no creatable children.");
        var meetingRoom = admits.Single(a => a.Name == "Meeting room");
        var roomName = $"Room {Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Href("children"), roomName, meetingRoom.MaskId);

        // The bookings address comes from the room's RESOURCE (ADR 0543) — the listing row advertises what
        // browsing needs, so the dialog's opener follows `self` once and takes `bookings` from there.
        var room = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(c => c.Name == roomName);
        var detail = await api.Documents.GetDocumentDetailAsync(room.Href("self"));
        return (api, detail.Href("bookings"), roomName);
    }

    [Fact]
    public async Task Booking_lists_the_slot_and_a_conflict_shows_the_localized_refusal()
    {
        var (api, bookingsHref, roomName) = await RoomAsync();

        var vm = new BookingDialogViewModel(api, bookingsHref, roomName);
        await vm.LoadAsync();
        Assert.True(vm.CanBook);
        Assert.Empty(vm.Bookings);

        vm.BookingDate = DateTime.Today.AddDays(7);
        vm.StartTime = new TimeSpan(9, 0, 0);
        vm.EndTime = new TimeSpan(10, 0, 0);
        vm.Purpose = "Standup";
        await vm.BookCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Bookings);
        Assert.True(row.Row.CanCancel);
        Assert.Equal("Standup", row.Row.Purpose);
        Assert.Empty(vm.Status);

        // The same slot again: refused, and the message is ApiErrorText's localized sentence — never the
        // server's English detail (issue #424).
        vm.EndTime = new TimeSpan(9, 30, 0);
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Equal(SimplArchive.Localization.ApiErrorText.For("BOOKING_SLOT_CONFLICT"), vm.Status);
        Assert.Single(vm.Bookings);
    }

    [Fact]
    public async Task Cancelling_keeps_the_row_as_history_and_frees_the_slot()
    {
        var (api, bookingsHref, roomName) = await RoomAsync();

        var vm = new BookingDialogViewModel(api, bookingsHref, roomName);
        await vm.LoadAsync();
        vm.BookingDate = DateTime.Today.AddDays(8);
        vm.StartTime = new TimeSpan(14, 0, 0);
        vm.EndTime = new TimeSpan(15, 0, 0);
        await vm.BookCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Bookings);

        await vm.CancelBookingCommand.ExecuteAsync(row);

        // Cancelled is history, not erasure: the row stays, says so in the user's language, and offers no
        // second cancel; the slot is free again.
        var cancelled = Assert.Single(vm.Bookings);
        Assert.Equal(SimplArchive.Localization.Strings.Get("BookStatusCancelled"), cancelled.StatusText);
        Assert.False(cancelled.Row.CanCancel);

        await vm.BookCommand.ExecuteAsync(null);
        Assert.Empty(vm.Status);
        Assert.Equal(2, vm.Bookings.Count);
    }
}
