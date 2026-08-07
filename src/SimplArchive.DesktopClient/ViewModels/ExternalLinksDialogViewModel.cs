using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs both desktop external-link dialogs (ADR 0546, issue #385):
//
//  · per-document — create a link for THIS document, list the live ones, extend or revoke;
//  · cross-document ("My external links") — everything the caller has shared, with a tenant-admin picker.
//
// One view-model rather than two, because the list, extend and revoke behaviour is identical; only the source
// href and whether creation is offered differ. Interactive — does its own API calls.
public partial class ExternalLinksDialogViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;

    // The collection href, exactly as the server advertised it (ADR 0543). Never composed here.
    private readonly string _linksHref;

    private readonly bool _crossDocument;

    public ExternalLinksDialogViewModel(SimplArchiveApiClient api, string linksHref, string documentName, bool crossDocument = false)
    {
        _api = api;
        _linksHref = linksHref;
        _crossDocument = crossDocument;
        DocumentName = documentName;
    }

    public string DocumentName { get; }

    // Only the per-document dialog offers creation; the cross-document one is a review surface.
    public bool ShowCreate => !_crossDocument;

    public ObservableCollection<SimplArchiveApiClient.ExternalLinkInfo> Links { get; } = [];

    public ObservableCollection<SimplArchiveApiClient.UserOptionInfo> Users { get; } = [];

    [ObservableProperty] private SimplArchiveApiClient.UserOptionInfo? _selectedUser;

    [ObservableProperty] private DateTimeOffset? _expiry = DateTimeOffset.Now.AddDays(30);

    [ObservableProperty] private int? _maxAccesses;

    [ObservableProperty] private bool _canCreate;

    [ObservableProperty] private bool _canViewOthers;

    [ObservableProperty] private bool _forceDownload;

    // Shown once, prominently: the token is a live credential and the list endpoints never return it, so if the
    // sharer loses this URL the only remedy is to revoke and create another (ADR 0546).
    [ObservableProperty] private string? _createdUrl;

    [ObservableProperty] private string _status = "";

    public string CreatedUrlWithOptions =>
        CreatedUrl is null ? "" : ForceDownload ? CreatedUrl + "?download=true" : CreatedUrl;

    partial void OnForceDownloadChanged(bool value) => OnPropertyChanged(nameof(CreatedUrlWithOptions));

    partial void OnCreatedUrlChanged(string? value) => OnPropertyChanged(nameof(CreatedUrlWithOptions));

    public async Task LoadAsync()
    {
        var result = _crossDocument
            ? await _api.GetMyExternalLinksAsync(_linksHref, SelectedUser?.Id == Guid.Empty ? null : SelectedUser?.Id)
            : await _api.GetExternalLinksAsync(_linksHref);

        Links.Clear();
        foreach (var link in result.Links)
        {
            Links.Add(link);
        }

        CanCreate = result.CanCreate;
        CanViewOthers = result.CanViewOthers;

        if (_crossDocument && CanViewOthers && Users.Count == 0)
        {
            // Only a tenant admin can filter by another person, so the directory is fetched only for them —
            // and only once. Reuses the existing users listing rather than adding a parallel endpoint.
            Users.Add(new SimplArchiveApiClient.UserOptionInfo(Guid.Empty, Strings.Get("ExtLinkMine")));
            foreach (var user in await _api.GetUsersAsync())
            {
                Users.Add(new SimplArchiveApiClient.UserOptionInfo(user.Id, user.Name));
            }
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var created = await _api.CreateExternalLinkAsync(_linksHref, Expiry, MaxAccesses);
        if (created is null)
        {
            // The tenant switch being off and the right being absent both land here. The dialog says the feature
            // is unavailable rather than guessing which, since the remedy differs and guessing wrong misleads.
            Status = Strings.Get("ExtLinkDisabled");
            return;
        }

        CreatedUrl = created.Url;
        Status = Strings.Get("ExtLinkShownOnce");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RevokeAsync(SimplArchiveApiClient.ExternalLinkInfo? link)
    {
        if (link?.RevokeHref is not { } href)
        {
            return;
        }

        if (await _api.RevokeExternalLinkAsync(href, link.Etag))
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task ExtendAsync(SimplArchiveApiClient.ExternalLinkInfo? link)
    {
        // 90 days from today — the server measures it, so the client does not have to know the rule twice.
        if (link?.ExtendHref is not { } href)
        {
            return;
        }

        if (await _api.ExtendExternalLinkAsync(href, 90, link.Etag))
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task FilterAsync() => await LoadAsync();
}
