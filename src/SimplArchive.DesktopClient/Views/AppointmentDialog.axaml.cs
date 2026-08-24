using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
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

        // Which of the two things this window is — the same dialog serves New and Edit (#631).
        if (model.IsCreate)
        {
            Title = Strings.Get("CalendarNew");
        }

        ReminderText.Text = model.ReminderCount == 1
            ? Strings.Get("ApptReminderOne")
            : string.Format(CultureInfo.CurrentCulture, Strings.Get("ApptReminderMany"), model.ReminderCount);
    }


    /// <summary>
    /// Fetches the raw source the first time the disclosure is opened (#648).
    /// </summary>
    /// <remarks>
    /// Supplied as a lambda by the caller rather than done here: this window owns no api client, which is what
    /// keeps the load and the save testable without a display. Lazy because a card carrying a photo is hundreds
    /// of kilobytes and most edits never open the box.
    /// </remarks>
    public Func<Task>? RawLoader { get; set; }

    private void OnRawExpanding(object? sender, Avalonia.Interactivity.CancelRoutedEventArgs e) =>
        Safe.Fire(async () =>
        {
            if (RawLoader is { } load)
            {
                await load();
            }
        });

    private void OnSave(object? sender, RoutedEventArgs e) => Close(DataContext as AppointmentEditViewModel);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
