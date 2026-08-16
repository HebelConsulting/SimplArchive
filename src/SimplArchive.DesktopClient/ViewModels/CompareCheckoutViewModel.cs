using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Compare-checkout dialog (ADR 0517) — an inline unified diff of a checked-out document's current
// version vs its working copy in check-out (the cloud stash), plus an optional "Beyond Compare" launch when that
// tool is installed. Fixed two sides (no version pickers), mirroring CompareVersionsViewModel.
public sealed partial class CompareCheckoutViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private Guid _documentId;
    private string _fileExtension = "";
    private string? _stashDownloadUrl;

    [ObservableProperty] private string _documentName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _notAvailable;

    public ObservableCollection<DiffLineViewModel> Lines { get; } = [];

    public async Task SetupAsync(SimplArchiveApiClient api, CheckoutClient.CheckoutItem checkout, string documentName, string fileExtension, string? stashDownloadUrl)
    {
        _api = api;
        _documentId = checkout.Id;
        _fileExtension = fileExtension;
        _stashDownloadUrl = stashDownloadUrl;
        DocumentName = documentName;

        Lines.Clear();
        NotAvailable = false;
        Status = Strings.Get("StComparing");
        try
        {
            var cmp = await api.Checkout.GetCheckoutComparisonAsync(checkout);
            if (!cmp.Available)
            {
                NotAvailable = true;
                Status = Strings.Get("StCompareUnavailable");
                return;
            }

            foreach (var l in cmp.Lines)
            {
                Lines.Add(new DiffLineViewModel(l.Op, l.Text));
            }

            Status = string.Format(Strings.Get("StLines"), cmp.Lines.Count);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrCompare"), e.Message);
        }
    }

    [RelayCommand]
    private async Task OpenInBeyondCompare()
    {
        if (_api is null)
        {
            return;
        }

        // The staging, the left/right order and the not-installed branch (ADR 0518) live in CheckoutDiffLauncher,
        // which the Check-out row's own button also calls — one implementation, so the two cannot drift apart.
        Status = Strings.Get("StOpeningBc");
        Status = await CheckoutDiffLauncher.OpenAsync(_api, _documentId, _fileExtension, _stashDownloadUrl);
    }
}
