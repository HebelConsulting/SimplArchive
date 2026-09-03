using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Bookings… dialog (ADR 0735, the meeting-room half of ADR 0743's slice-1 scope): list a
// bookable resource's bookings, book a slot, cancel one. The ReminderDialog's shape — rows above, the create
// form below — because the two dialogs answer the same kind of question about a document. Interactive: does
// its own API calls via the SimplArchiveApiClient it's constructed with.
public partial class BookingDialogViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;
    private readonly string _bookingsHref;

    public BookingDialogViewModel(SimplArchiveApiClient api, string bookingsHref, string resourceName)
    {
        _api = api;
        _bookingsHref = bookingsHref;
        ResourceName = resourceName;
    }

    public string ResourceName { get; }

    /// <summary>One row, with its status localized here — the wire carries the enum name (issue #424).</summary>
    public sealed record BookingRowView(BookingsClient.BookingRow Row, string TimeText, string StatusText);

    public ObservableCollection<BookingRowView> Bookings { get; } = [];

    [ObservableProperty] private DateTime? _bookingDate = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan? _startTime = new(9, 0, 0);
    [ObservableProperty] private TimeSpan? _endTime = new(10, 0, 0);
    [ObservableProperty] private string _purpose = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _canBook;

    public async Task LoadAsync()
    {
        Bookings.Clear();
        try
        {
            var (rows, canBook) = await _api.Bookings.ListAsync(_bookingsHref);
            CanBook = canBook;
            foreach (var row in rows)
            {
                var statusKey = row.Status == "Cancelled" ? "BookStatusCancelled" : "BookStatusActive";
                Bookings.Add(new BookingRowView(
                    row,
                    $"{row.StartsAt.ToLocalTime():g} – {row.EndsAt.ToLocalTime():t}",
                    Strings.Get(statusKey)));
            }
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task Book()
    {
        if (BookingDate is not { } date || StartTime is not { } start || EndTime is not { } end)
        {
            return;
        }

        // Local wall-clock in, real instants out — the offset is this machine's, and the server compares
        // instants (ADR 0735's [start, end) semantics).
        var startsAt = new DateTimeOffset(DateOnly.FromDateTime(date), TimeOnly.FromTimeSpan(start), TimeZoneInfo.Local.GetUtcOffset(date));
        var endsAt = new DateTimeOffset(DateOnly.FromDateTime(date), TimeOnly.FromTimeSpan(end), TimeZoneInfo.Local.GetUtcOffset(date));

        try
        {
            await _api.Bookings.BookAsync(_bookingsHref, startsAt, endsAt, string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim());
            Status = string.Empty;
            await LoadAsync();
        }
        catch (ApiActionException e)
        {
            // Already localized (ApiErrorText) — the slot-conflict sentence is the one users will meet.
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task CancelBooking(BookingRowView? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            await _api.Bookings.CancelAsync(row.Row);
            Status = string.Empty;
            await LoadAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }
}
