using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

// The meeting-room booking surface (ADR 0735; ADR 0743's slice-1 client half): a Bookings… button in the
// detail pane, present exactly when the selected document's resource advertised the `bookings` rel — the
// rel's presence IS the affordance (ADR 0543), so a plain folder never shows it and nothing needs to know
// what a "room" is.
public partial class MainWindowViewModel
{
    /// <summary>The dialog is view-owned, so the view supplies it — a settable callback (ADR 0730): a
    /// forgotten assignment disables a visible button, which is the loud failure mode.</summary>
    public Func<BookingDialogViewModel, Task>? ShowBookingDialog { get; set; }

    /// <summary>True when the selected document advertises `bookings` — set with the detail links.</summary>
    public bool CanOpenBookings => _detailLinks?.ContainsKey("bookings") == true;

    [RelayCommand]
    private async Task OpenBookings()
    {
        if (_api is not { } api || ShowBookingDialog is null
            || _detailLinks is not { } links || !links.TryGetValue("bookings", out var bookingsHref))
        {
            return;
        }

        await ShowBookingDialog(new BookingDialogViewModel(api, bookingsHref, _detailDocumentName ?? string.Empty));
    }
}
