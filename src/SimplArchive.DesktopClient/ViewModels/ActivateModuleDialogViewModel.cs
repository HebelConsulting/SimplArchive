using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Activate/Renew dialog (ADRs 0740/0743): a dropdown of the filed license documents (the
/// `license-documents` listing — documents wearing the Module-license mask, newest first) and the act
/// itself, PUT against the module row's `license` rel. A refusal lands beside the form (ADR 0550), as the
/// localized sentence for the server's precise error code.
/// </summary>
public sealed partial class ActivateModuleDialogViewModel(
    AdminClient admin, ModuleRowViewModel module, string? licenseDocumentsHref) : ObservableObject
{
    public string ModuleName => module.DisplayName;

    public string ConfirmLabel => module.ActivateLabel.TrimEnd('…');

    public ObservableCollection<AdminClient.LicenseDocumentInfo> LicenseDocuments { get; } = [];

    [ObservableProperty] private AdminClient.LicenseDocumentInfo? _selectedDocument;

    [ObservableProperty] private bool _loaded;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True after a successful activation — the opener reloads the modules list on it.</summary>
    public bool Activated { get; private set; }

    public bool NoDocuments => Loaded && LicenseDocuments.Count == 0;

    public event Action? CloseRequested;

    public async Task LoadAsync()
    {
        if (licenseDocumentsHref is null)
        {
            Loaded = true;
            OnPropertyChanged(nameof(NoDocuments));
            return;
        }

        try
        {
            foreach (var document in await admin.GetLicenseDocumentsAsync(licenseDocumentsHref))
            {
                LicenseDocuments.Add(document);
            }

            SelectedDocument = LicenseDocuments.FirstOrDefault();
        }
        catch (Exception)
        {
            Status = Strings.Get("ApiErrGeneric");
        }

        Loaded = true;
        OnPropertyChanged(nameof(NoDocuments));
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (module.Module.LicenseHref is not { } licenseHref || SelectedDocument is not { } document)
        {
            return;
        }

        try
        {
            await admin.ActivateModuleAsync(licenseHref, document.Id);
            Activated = true;
            CloseRequested?.Invoke();
        }
        catch (ApiActionException exception)
        {
            Status = exception.Message; // already the localized sentence for the server's error code
        }
        catch (Exception)
        {
            Status = Strings.Get("ApiErrGeneric");
        }
    }
}
