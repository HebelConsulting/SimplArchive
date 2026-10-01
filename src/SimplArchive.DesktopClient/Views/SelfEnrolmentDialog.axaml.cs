using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Enrolling your OWN credential, where the tenant's modules offer it (ABI 1.3, ADR 0864, #1502).
/// </summary>
/// <remarks>
/// <para>
/// <b>The core renders this on the module's behalf</b> — a module contributes controllers and root links,
/// never UI (ADR 0737), so what it declares is <i>where</i>, <i>when</i>, what it is <i>called</i> and what to
/// <i>ask for</i>, and this dialog is the core's rendering of that. Nothing here names a module: a second one
/// declaring an enrolment appears with no change to this file, which is the whole point of the declaration
/// over a core dialog that learned one module's route.
/// </para>
/// <para>
/// <b>This is the canonical surface</b> (ADR 0511); the web dialog mirrors it.
/// </para>
/// </remarks>
public partial class SelfEnrolmentDialog : Window
{
    private readonly SimplArchiveApiClient _api;
    private ProfileClient.SelfEnrolment? _enrolment;
    private string? _pem;

    // Parameterless ctor for the XAML designer/loader only.
    public SelfEnrolmentDialog() : this(null!)
    {
    }

    public SelfEnrolmentDialog(SimplArchiveApiClient api)
    {
        InitializeComponent();
        _api = api;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var offered = await _api.Profile.GetSelfEnrolmentsAsync();

            // THE FIRST, because the core orders them and today exactly one module declares one. A chooser
            // for a list of one is a step with no outcome; when a second module appears this becomes a
            // selector, and the resource already carries everything such a selector would need.
            _enrolment = offered.FirstOrDefault();

            if (_enrolment is null)
            {
                // Reachable only if the rel was advertised and the list then came back empty — a module
                // deactivated between opening the menu and opening this window. Saying so beats an empty form.
                SubjectText.Text = Strings.Get("EnrolNone");
                return;
            }

            SubjectText.Text = _enrolment.Title;
            DisabledText.IsVisible = !_enrolment.Enabled;
            FormPanel.IsVisible = _enrolment.Enabled;
            LabelBox.IsVisible = !string.IsNullOrWhiteSpace(_enrolment.LabelField);
        }
        catch (Exception)
        {
            // Its OWN message. The first draft borrowed WebDAV's, and a headless render of this dialog showed
            // "Could not load WebDAV status." inside an enrolment window — which is the whole argument for
            // rendering a Window rather than trusting that its strings are the right ones.
            StatusText.Text = Strings.Get("EnrolLoadError");
        }
    }

    private void OnChoose(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("EnrolChoose"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Get("EnrolFileType"))
                {
                    Patterns = ["*.pem", "*.crt", "*.cer", "*.der"],
                },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new System.IO.MemoryStream();
        await stream.CopyToAsync(buffer);

        Accept(buffer.ToArray(), files[0].Name);
    });

    /// <summary>
    /// Takes the chosen file if it is a certificate and nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A private key is refused HERE, before anything is sent</b>, whatever the module's own policy says.
    /// The server refuses key material too — but by then it has crossed the wire, which is the one thing this
    /// is about. The rule itself lives in <see cref="EnrolledCertificate"/> so that this dialog, the web one
    /// and <c>saconsole</c> cannot drift about what they refuse.
    /// </para>
    /// <para>
    /// <b>DER is converted rather than refused</b>, because a certificate exported from a card tool is as
    /// likely to be one as the other, and refusing it would be refusing the FORMAT rather than the content.
    /// That conversion is deliberately not in the shared rule: a browser cannot parse a certificate at all.
    /// </para>
    /// </remarks>
    internal void Accept(byte[] bytes, string fileName)
    {
        var text = Encoding.UTF8.GetString(bytes);

        if (EnrolledCertificate.Refusal(text) is { } refusal)
        {
            if (refusal == CertificateRefusal.NotACertificate && TryDer(bytes) is { } converted)
            {
                text = converted;
            }
            else
            {
                StatusText.Text = Strings.Get(refusal switch
                {
                    CertificateRefusal.PrivateKey => "EnrolRefusedPrivateKey",
                    CertificateRefusal.Empty => "EnrolRefusedEmpty",
                    _ => "EnrolRefusedNotCertificate",
                });

                _pem = null;
                ChosenText.IsVisible = false;
                EnrolButton.IsEnabled = false;
                return;
            }
        }

        _pem = text;
        ChosenText.Text = fileName;
        ChosenText.IsVisible = true;
        StatusText.Text = string.Empty;
        EnrolButton.IsEnabled = true;
    }

    private static string? TryDer(byte[] bytes)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(bytes);
            return certificate.ExportCertificatePem();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private void OnEnrol(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_enrolment is not { Enabled: true, EnrolHref: { Length: > 0 } } || _pem is null)
        {
            return;
        }

        EnrolButton.IsEnabled = false;
        StatusText.Text = Strings.Get("EnrolSending");

        using var response = await _api.Profile.EnrolAsync(_enrolment, _pem, LabelBox.Text);
        if (response.IsSuccessStatusCode)
        {
            StatusText.Text = Strings.Get("EnrolDone");
            FormPanel.IsVisible = false;
            return;
        }

        StatusText.Text = await RefusalAsync(response);
        EnrolButton.IsEnabled = true;
    });

    /// <summary>
    /// What the module said, in the module's own words.
    /// </summary>
    /// <remarks>
    /// <b>The refusals stay distinguishable</b> (ADR 0859) because their fixes differ and only one of them is
    /// the user's: self-service switched off is an administrator's, a certificate the product's key rules
    /// reject is a re-issue, one the tenant's policy rejects is a different CA, and already-enrolled is
    /// nothing at all. So the module's own localized detail is shown rather than a generic "could not enrol",
    /// which would send all four to the same place.
    /// </remarks>
    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            if (problem.TryGetProperty("detail", out var detail)
                && detail.GetString() is { Length: > 0 } said)
            {
                return said;
            }
        }
        catch (JsonException)
        {
            // Not a problem document — fall through to the generic line rather than showing raw bytes.
        }

        return response.StatusCode == HttpStatusCode.Forbidden
            ? Strings.Get("EnrolDisabled")
            : Strings.Get("EnrolFailed");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
