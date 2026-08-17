using System;
using System.IO;
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

        // Which account this is. Costs no request of its own — it rides in the same "me" read the rels use.
        try
        {
            EmailText.Text = await _api.Profile.MyEmailAsync() ?? "";
        }
        catch (Exception)
        {
            EmailText.Text = "";
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

    private void OnSave(object? sender, RoutedEventArgs e) => Close(Editor.CroppedPng());

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
