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

    public ObservableCollection<ExternalLinksClient.ExternalLinkInfo> Links { get; } = [];

    public ObservableCollection<UserOptionInfo> Users { get; } = [];

    [ObservableProperty] private UserOptionInfo? _selectedUser;

    // UtcNow, not Now: a DateTimeOffset carrying a local offset is a valid instant but Postgres stores instants
    // with offset 0 only, so sending one used to 500 the create endpoint for anybody not sitting in UTC. The
    // server now normalises inbound timestamps, but sending what we mean is still the honest thing — the user
    // picks a DATE, and its zone is not part of what they chose.
    [ObservableProperty] private DateTimeOffset? _expiry = DateTimeOffset.UtcNow.AddDays(30);

    [ObservableProperty] private int? _maxAccesses;

    [ObservableProperty] private bool _canCreate;

    [ObservableProperty] private bool _canViewOthers;

    // Shown once, prominently: the token is a live credential and the list endpoints never return it, so if the
    // sharer loses this URL the only remedy is to revoke and create another (ADR 0546).
    [ObservableProperty] private string? _createdUrl;

    [ObservableProperty] private string _status = string.Empty;

    // Set by the host window: opening a child dialog and moving the workbench are both things a dialog cannot do
    // for itself, so they arrive as callbacks rather than as a dependency on the main view-model.
    public Func<ExternalLinkDetailDialogViewModel, Task>? ShowDetailDialog { get; set; }

    // (documentId for selecting the row after navigation, the document's advertised address, its parent's.)
    public Action<Guid, string?, string?>? GoToDocument { get; set; }

    // Set by the host window, so "Go to" can dismiss this dialog before the workbench moves behind it.
    public Action? RequestClose { get; set; }

    // "Go to" only appears in the cross-document list; in the per-document one it would select the document the
    // reader is already looking at.
    public bool ShowGoTo => !ShowCreate;

    // One URL, no variants: opening it lands on a page offering both Open and Download, so the RECIPIENT decides
    // how to take the document. The sharer used to make that choice for them at creation time, from a dialog that
    // could not know what the other person wanted (ADR 0546).
    public string CreatedUrlWithOptions => CreatedUrl ?? "";

    partial void OnCreatedUrlChanged(string? value) => OnPropertyChanged(nameof(CreatedUrlWithOptions));

    public async Task LoadAsync()
    {
        var result = _crossDocument
            ? await _api.ExternalLinks.GetMyExternalLinksAsync(_linksHref, SelectedUser?.Id == Guid.Empty ? null : SelectedUser?.Id)
            : await _api.ExternalLinks.GetExternalLinksAsync(_linksHref);

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
            Users.Add(new UserOptionInfo(Guid.Empty, Strings.Get("ExtLinkMine")));
            foreach (var user in await _api.Admin.GetUsersAsync())
            {
                Users.Add(new UserOptionInfo(user.Id, user.Name));
            }
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        ExternalLinksClient.ExternalLinkInfo? created;
        try
        {
            created = await _api.ExternalLinks.CreateExternalLinkAsync(_linksHref, Expiry, MaxAccesses);
        }
        catch (ApiActionException e)
        {
            // A genuine failure, reported as one. This used to fall into the branch below and claim the tenant
            // switch was off — which had the reader checking a setting that was already correct.
            Status = e.Message;
            return;
        }

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
    private async Task RevokeAsync(ExternalLinksClient.ExternalLinkInfo? link)
    {
        if (link?.RevokeHref is not { } href)
        {
            return;
        }

        if (await _api.ExternalLinks.RevokeExternalLinkAsync(href, link.Etag))
        {
            await LoadAsync();
        }
    }

    // The link's own details, read-only, with renewal as the one thing still open to change. Renewal lives THERE
    // rather than on the row because it is now two decisions — how long, and how many more times — and a
    // row-sized button cannot ask for either without guessing on the reader's behalf.
    [RelayCommand]
    private async Task ShowAsync(ExternalLinksClient.ExternalLinkInfo? link)
    {
        if (link is null || ShowDetailDialog is null)
        {
            return;
        }

        // The document name comes from THIS dialog, not from the row: the per-document listing sends an empty
        // documentName (its caller already knows the document), which the detail dialog rendered as a blank
        // "Document". In the cross-document list the row does carry it, and the constructor prefers whichever
        // is non-empty.
        var detail = new ExternalLinkDetailDialogViewModel(_api, link, _crossDocument ? null : DocumentName);
        await ShowDetailDialog(detail);

        if (detail.Renewed)
        {
            await LoadAsync();
        }
    }

    // Hands the document back to the workbench, which owns the tree and list panes; this dialog cannot see them.
    // Only meaningful in the cross-document list — the per-document one is already sitting on the document.
    [RelayCommand]
    private void GoTo(ExternalLinksClient.ExternalLinkInfo? link)
    {
        if (link is null)
        {
            return;
        }

        GoToDocument?.Invoke(link.DocumentId, link.DocumentHref, link.ParentHref);
    }

    [RelayCommand]
    private async Task FilterAsync() => await LoadAsync();
}
