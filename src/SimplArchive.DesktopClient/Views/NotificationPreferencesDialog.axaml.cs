using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

// Per-type email-notification preferences (ADR "Notification preferences"). In-app notifications always show;
// these toggles only control which types are also emailed. The deadline/compliance escalations aren't listed
// (always emailed). ShowDialog returns nothing — the account menu just opens it.
public partial class NotificationPreferencesDialog : Window
{
    private readonly SimplArchiveApiClient _api;
    private readonly ObservableCollection<PrefRow> _rows = [];

    // Parameterless ctor for the XAML designer/loader only.
    public NotificationPreferencesDialog() : this(null!)
    {
    }

    public NotificationPreferencesDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        PrefList.ItemsSource = _rows;
        Opened += async (_, _) => await LoadAsync();
    }

    public sealed class PrefRow
    {
        public int Type { get; init; }
        public string Label { get; init; } = string.Empty;
        public bool EmailEnabled { get; set; }
    }

    private async Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            _rows.Clear();
            foreach (var p in await _api.Profile.GetNotificationPreferencesAsync())
            {
                _rows.Add(new PrefRow { Type = p.Type, Label = Label(p.TypeName), EmailEnabled = p.EmailEnabled });
            }

            Error.IsVisible = false;
        }
        catch (Exception)
        {
            Error.Text = "Could not load your notification preferences.";
            Error.IsVisible = true;
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            await _api.Profile.SetNotificationPreferencesAsync(
                System.Linq.Enumerable.Select(_rows, r => new ProfileClient.NotificationPreferenceInfo(r.Type, "", r.EmailEnabled)));
            Close();
        }
        catch (Exception)
        {
            Error.Text = "Could not save your notification preferences.";
            Error.IsVisible = true;
            SaveButton.IsEnabled = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private static string Label(string typeName) => typeName switch
    {
        "ReviewAssigned" => "A review is assigned to me",
        "WorkflowApproved" => "A document I submitted is approved",
        "WorkflowRejected" => "A document I submitted is rejected",
        "WorkflowReleased" => "A document I submitted is released",
        "ChatMessagePosted" => "A chat message is posted on my document",
        "AccessGranted" => "I'm granted access to a document",
        _ => typeName,
    };
}
