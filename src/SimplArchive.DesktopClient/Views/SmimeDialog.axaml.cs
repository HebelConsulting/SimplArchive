using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The self-service S/MIME certificate (#1332, ADR 0816): upload the public half or generate a complete
// identity; once one is set both entrances disable until Delete — the server enforces the same machine.
// This is the canonical surface (ADR 0511); the web SmimeDialog mirrors it.
public partial class SmimeDialog : Window
{
    private readonly SimplArchiveApiClient _api;
    private ProfileClient.SmimeInfo? _status;
    private byte[]? _generatedP12;
    private byte[]? _generatedProfile;
    private string _fileNameStem = "identity";

    // Parameterless ctor for the XAML designer/loader only.
    public SmimeDialog() : this(null!)
    {
    }

    public SmimeDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            Apply(await _api.Profile.GetSmimeAsync());
        }
        catch
        {
            StatusText.Text = Strings.Get("WdLoadError");
        }
    }

    private void Apply(ProfileClient.SmimeInfo status)
    {
        _status = status;
        ProvisionedText.IsVisible = !status.SelfService;
        SelfServicePanel.IsVisible = status.SelfService;
        EnabledPanel.IsVisible = status.Enabled;
        NoneText.IsVisible = status.SelfService && !status.Enabled;
        SubjectBox.Text = status.Subject ?? string.Empty;
        NotAfterBox.Text = status.NotAfter?.ToString("yyyy-MM-dd") ?? string.Empty;
        DeleteButton.IsVisible = status.SelfService && status.Enabled;
        UploadButton.IsEnabled = status.SelfService && !status.Enabled;
        P12PasswordBox.IsEnabled = status.SelfService && !status.Enabled;
        GenerateButton.IsEnabled = status.SelfService && !status.Enabled
            && !string.IsNullOrWhiteSpace(P12PasswordBox.Text);
    }

    private void OnPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (_status is { } status)
        {
            GenerateButton.IsEnabled = status.SelfService && !status.Enabled
                && !string.IsNullOrWhiteSpace(P12PasswordBox.Text);
        }
    }

    private void OnUpload(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_status is not { } status)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("SmimeUpload"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Certificate") { Patterns = ["*.pem", "*.crt", "*.cer", "*.der"] },
            ],
        });
        if (files.Count != 1)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Apply(await _api.Profile.UploadSmimeCertificateAsync(status, buffer.ToArray()));
            StatusText.Text = string.Empty;
        }
        catch
        {
            StatusText.Text = Strings.Get("SmimeUploadError");
        }
    });

    private void OnGenerate(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_status is not { } status || string.IsNullOrWhiteSpace(P12PasswordBox.Text))
        {
            return;
        }

        try
        {
            var generated = await _api.Profile.GenerateSmimeIdentityAsync(status, P12PasswordBox.Text!);
            Apply(generated);
            _generatedP12 = generated.Pkcs12 is { } p12 ? Convert.FromBase64String(p12) : null;
            _generatedProfile = generated.MobileConfig is { } profile ? Convert.FromBase64String(profile) : null;
            _fileNameStem = generated.FileNameStem is { Length: > 0 } stem ? stem : _fileNameStem;
            GeneratedPanel.IsVisible = _generatedP12 is not null;
            P12PasswordBox.Text = string.Empty;
            StatusText.Text = string.Empty;
        }
        catch
        {
            StatusText.Text = Strings.Get("SmimeGenerateError");
        }
    });

    private void OnDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_status is not { } status)
        {
            return;
        }

        try
        {
            await _api.Profile.DeleteSmimeCertificateAsync(status);
            _generatedP12 = null;
            _generatedProfile = null;
            GeneratedPanel.IsVisible = false;
            await LoadAsync();
        }
        catch
        {
            StatusText.Text = Strings.Get("SmimeDeleteError");
        }
    });

    private void OnSaveP12(object? sender, RoutedEventArgs e) =>
        Safe.Fire(() => SaveAsync($"{_fileNameStem}.p12", _generatedP12));

    private void OnSaveProfile(object? sender, RoutedEventArgs e) =>
        Safe.Fire(() => SaveAsync($"{_fileNameStem}.mobileconfig", _generatedProfile));

    private async Task SaveAsync(string suggestedName, byte[]? bytes)
    {
        if (bytes is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = suggestedName,
            SuggestedFileName = suggestedName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
