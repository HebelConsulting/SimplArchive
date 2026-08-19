using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The structured appointment editor (#564, ADR 0631). ShowDialog<AppointmentEditViewModel?> returns the edited
// form on Save and null on Cancel — the CALLER saves, so this window owns no api client, matching ContactDialog.
public partial class AppointmentDialog : Window
{
    // Parameterless ctor for the XAML designer/loader only.
    public AppointmentDialog() : this(new AppointmentEditViewModel())
    {
    }

    public AppointmentDialog(AppointmentEditViewModel model)
    {
        DataContext = model;
        InitializeComponent();

        // The zone the times are IN, spelled out. Without it an unconverted wall clock is ambiguous to anyone
        // reading it from elsewhere — which is the honest cost of not converting (ADR 0631 decision 5).
        // A floating time says so instead: it means "this time, wherever you are", and naming a zone it does
        // not have would be a different promise.
        ZoneText.Text = model.TimeZoneId is { Length: > 0 } zone
            ? string.Format(CultureInfo.CurrentCulture, Strings.Get("ApptTimesAreIn"), zone)
            : Strings.Get("ApptTimesAreFloating");

        ReminderText.Text = model.ReminderCount == 1
            ? Strings.Get("ApptReminderOne")
            : string.Format(CultureInfo.CurrentCulture, Strings.Get("ApptReminderMany"), model.ReminderCount);
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(DataContext as AppointmentEditViewModel);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
