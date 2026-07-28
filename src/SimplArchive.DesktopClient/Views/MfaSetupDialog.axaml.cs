using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

// Two-factor (TOTP) enrollment dialog (ADR "MFA (interactive login, TOTP)"). Drives enroll/enable against the
// API interactively: on open it enrolls (QR + secret), on Enable it confirms the code and shows the one-time
// recovery codes. ShowDialog<bool> returns true if MFA was enabled.
public partial class MfaSetupDialog : Window
{
    private readonly SimplArchiveApiClient _api;

    // Parameterless ctor for the XAML designer/loader only.
    public MfaSetupDialog() : this(null!)
    {
    }

    public MfaSetupDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        Opened += async (_, _) => await BeginEnrollAsync();
    }

    private async System.Threading.Tasks.Task BeginEnrollAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var enroll = await _api.EnrollMfaAsync();
            SecretRun.Text = enroll.Secret;
            var base64 = enroll.QrDataUrl.Contains(',') ? enroll.QrDataUrl[(enroll.QrDataUrl.IndexOf(',') + 1)..] : enroll.QrDataUrl;
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            QrImage.Source = new Bitmap(stream);
            CodeBox.Focus();
        }
        catch (Exception)
        {
            Error.Text = "Could not start two-factor setup.";
            Error.IsVisible = true;
            EnableButton.IsEnabled = false;
        }
    }

    private async void OnEnable(object? sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text ?? "";
        if (code.Length == 0)
        {
            return;
        }

        Error.IsVisible = false;
        EnableButton.IsEnabled = false;
        try
        {
            var codes = await _api.EnableMfaAsync(code);
            EnrollPanel.IsVisible = false;
            RecoveryList.ItemsSource = codes;
            RecoveryPanel.IsVisible = true;
        }
        catch (ApiActionException ex)
        {
            Error.Text = ex.Message;
            Error.IsVisible = true;
            EnableButton.IsEnabled = true;
        }
        catch (Exception)
        {
            Error.Text = "Could not enable two-factor authentication.";
            Error.IsVisible = true;
            EnableButton.IsEnabled = true;
        }
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
