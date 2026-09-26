using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The third way to register a certificate (#1398, ADR 0831): read one off a card or token. Opened from
// SmimeDialog, which owns the registration itself — this window's whole job is to let the user say WHICH
// certificate, because a device may carry several and a computer may have several devices.
//
// The private key never leaves the device. What travels is the public certificate, which is exactly what the
// upload entrance sends too — only its provenance differs.
public partial class CardCertificateDialog : Window
{
    private readonly List<CardCertificateRow> _rows = [];
    private IReadOnlyList<CardCertificates.Found> _found = [];

    /// <summary>The PEM the user chose, or null if they closed without choosing.</summary>
    public string? ChosenPem { get; private set; }

    // Parameterless ctor for the XAML designer/loader only.
    public CardCertificateDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Load();
    }

    private void Load()
    {
        _rows.Clear();
        ChosenPem = null;
        RegisterButton.IsEnabled = false;

        if (CardCertificates.FindModule() is not { } module)
        {
            // Naming WHICH of the three things is missing is the point (#1353): "no module" sends the user to
            // install OpenSC, "no token" sends them to their pocket, and "no certificate" to their
            // administrator. One generic failure message would send all three to the wrong place.
            StatusText.Text = Strings.Get("CardNoModule");
            CertificateList.ItemsSource = _rows;
            return;
        }

        try
        {
            _found = CardCertificates.Read(module);
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "Reading the PKCS#11 module at {Module} failed", module);
            StatusText.Text = Strings.Get("CardReadFailed");
            CertificateList.ItemsSource = _rows;
            return;
        }

        foreach (var one in _found)
        {
            _rows.Add(new CardCertificateRow(
                one.Certificate.Subject,
                $"{Strings.Get("CardDevice")}: {one.TokenLabel} · {one.ObjectLabel}",
                ConcernText(CardCertificates.Concern(one.Certificate, DateTimeOffset.UtcNow)),
                one.Pem));
        }

        CertificateList.ItemsSource = _rows;
        StatusText.Text = _rows.Count > 0 ? string.Empty : Strings.Get("CardNoCertificate");

        // A device with exactly one certificate still has to be CONFIRMED rather than registered on sight: the
        // user is asked to check the subject, which is the same check the create-link dialog asks for before a
        // document is addressed to a key.
        if (_rows.Count == 1)
        {
            CertificateList.SelectedIndex = 0;
        }
    }

    private static string ConcernText(CardCertificates.Concerns concern) => concern switch
    {
        CardCertificates.Concerns.Expired => Strings.Get("CardConcernExpired"),
        CardCertificates.Concerns.NotYetValid => Strings.Get("CardConcernNotYetValid"),
        CardCertificates.Concerns.KeyUsage => Strings.Get("CardConcernKeyUsage"),
        _ => string.Empty,
    };

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        RegisterButton.IsEnabled = CertificateList.SelectedItem is CardCertificateRow;

    // The Browse fallback. Probing the usual OpenSC locations covers the common case; this covers the user who
    // installed it somewhere else, which is otherwise a dead end with no way forward from the dialog.
    private void OnChooseModule(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("CardChooseModule"),
            AllowMultiple = false,
        });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }

        CardCertificates.ModulePathOverride = path;
        Load();
    });

    private void OnRegister(object? sender, RoutedEventArgs e)
    {
        if (CertificateList.SelectedItem is CardCertificateRow row)
        {
            ChosenPem = row.Pem;
            Close();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>One certificate on one device — what the user needs to tell two apart, already localized.</summary>
/// <remarks>
/// Top-level rather than nested inside the dialog because compiled bindings need an <c>x:DataType</c>, and a
/// nested type is awkward to name from XAML. Nothing else constructs one.
/// </remarks>
public sealed record CardCertificateRow(string Subject, string Device, string Concern, string Pem)
{
    /// <summary>Whether there is anything to warn about — drives the amber line's visibility.</summary>
    public bool HasConcern => Concern.Length > 0;
}
