using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// "Edit profile…" (#464) — the single way into your own account, replacing the avatar menu's separate photo and
// password entries. It shows which account you are signed in as, the photo you currently have, an inline crop to
// replace it, and a button through to the existing password dialog.
//
// ShowDialog<byte[]?> returns a new photo to upload, or null if nothing changed — the caller uploads, exactly as
// it did for ProfilePhotoDialog, so the view-model path is unchanged.
public partial class EditProfileDialog : Window
{
    private readonly SimplArchiveApiClient? _api;

    // Parameterless ctor for the XAML designer/loader only.
    public EditProfileDialog() : this(null!)
    {
    }

    public EditProfileDialog(SimplArchiveApiClient api)
    {
        _api = api;
        InitializeComponent();

        Editor.ImageChanged += (_, _) => SaveButton.IsEnabled = Editor.HasImage;
        Opened += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        // Which account this is, and which zone they chose. Both cost no request of their own — they ride in
        // the same "me" read the rels use.
        try
        {
            EmailText.Text = await _api.Profile.MyEmailAsync() ?? "";
            await LoadTimeZonesAsync();
        }
        catch (Exception)
        {
            EmailText.Text = string.Empty;
        }

        // The photo is bearer-protected, so it is fetched with the authenticated client and decoded here — a
        // plain image source would send no token and get a 401.
        try
        {
            if (await _api.Profile.GetMyPhotoAsync() is { Length: > 0 } bytes)
            {
                using var ms = new MemoryStream(bytes);
                CurrentPhoto.Source = new Bitmap(ms);
                NoPhotoText.IsVisible = false;
            }
        }
        catch (Exception)
        {
            // No photo yet, or it could not be read: the placeholder text stays, which is the honest answer.
        }
    }

    // The existing password dialog, opened from here and applied IMMEDIATELY on its OK — not deferred to this
    // dialog's Save. A password change the user confirmed once and then lost by closing the profile window
    // would be the worst kind of surprise.
    private void OnChangePassword(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_api is null || await new ChangePasswordDialog().ShowDialog<ChangePasswordDialog.Result?>(this) is not { } result)
        {
            return;
        }

        try
        {
            await _api.Profile.ChangeMyPasswordAsync(result.Current, result.New);
            PasswordStatus.Text = Strings.Get("PwdChanged");
        }
        catch (Exception)
        {
            PasswordStatus.Text = Strings.Get("PwdChangeError");
        }
    });

    // A null Id is "follow my device", labelled with the device's own zone so that "I have not chosen" is a
    // visible, reversible state rather than an empty row the user has to guess the meaning of. ToString is the
    // label because a ComboBox renders its items with it, and a bare id would show nothing for that entry.
    private sealed record ZoneChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private bool _timeZonesLoaded;

    private async System.Threading.Tasks.Task LoadTimeZonesAsync()
    {
        if (_api is null)
        {
            return;
        }

        var stored = await _api.Profile.MyTimeZoneIdAsync();
        var choices = new List<ZoneChoice>
        {
            new(null, string.Format(Strings.Get("EpTimeZoneDevice"), SessionTimeZone.IanaId)),
        };
        choices.AddRange(SimplArchive.Presentation.TimeZoneChoices.All().Select(id => new ZoneChoice(id, id)));

        TimeZoneBox.ItemsSource = choices;
        TimeZoneBox.SelectedItem = choices.FirstOrDefault(c => c.Id == stored) ?? choices[0];

        // Only AFTER the initial selection is in place: SelectionChanged fires on the assignment above, and
        // writing the preference back in response to merely opening the dialog would be a PUT nobody asked for.
        _timeZonesLoaded = true;
    }

    private void OnTimeZoneChanged(object? sender, SelectionChangedEventArgs e) => Safe.Fire(async () =>
    {
        if (_api is null || !_timeZonesLoaded || TimeZoneBox.SelectedItem is not ZoneChoice chosen)
        {
            return;
        }

        try
        {
            // Stored AND applied to the running session: the panes behind this window show document dates in
            // that zone, and leaving them on the old one until the next sign-in would make the setting look
            // like it had not worked.
            await _api.Profile.SetMyTimeZoneAsync(chosen.Id);
            TimeZoneStatus.Text = Strings.Get("EpTimeZoneSaved");
        }
        catch (Exception)
        {
            TimeZoneStatus.Text = Strings.Get("EpTimeZoneError");
        }
    });

    private void OnSave(object? sender, RoutedEventArgs e) => Close(Editor.CroppedPng());

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
