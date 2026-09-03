using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The Bookings… dialog (ADR 0735): thin code-behind on the ReminderDialog pattern — construct with the
// view-model, load on open, close on Close.
public partial class BookingDialog : Window
{
    public BookingDialog()
    {
        InitializeComponent();
    }

    public BookingDialog(BookingDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
