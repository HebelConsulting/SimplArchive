using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// One external link, read-only, with the one thing still open to change: how long it stays usable and how often it
// may still be redeemed (ADR 0546). The web client's ExternalLinkDetailDialog is the same surface — ADR 0511 makes
// them one thing in two toolkits.
//
// There is deliberately no other editing. A link's document, creator and token are settled when it is created —
// the token most of all, which the server hands out exactly once, so this dialog cannot show it however much a
// reader might want it to. Renewing is the only live decision left, so it is the only control.
public sealed partial class ExternalLinkDetailDialogViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;

    public ExternalLinkDetailDialogViewModel(SimplArchiveApiClient api, SimplArchiveApiClient.ExternalLinkInfo link)
    {
        _api = api;
        Link = link;
        MaxAccesses = link.MaxAccesses;
    }

    public SimplArchiveApiClient.ExternalLinkInfo Link { get; }

    public string DocumentName => Link.DocumentName;

    public string CreatedByName => Link.CreatedByName;

    public string Expires => Link.ExpiresAt.ToLocalTime().ToString("g");

    public string Accesses => $"{Link.AccessCount} / {Link.MaxAccesses?.ToString() ?? Strings.Get("ExtLinkUnlimited")}";

    public string UrlNote => Strings.Get("ExtLinkUrlNotShown");

    // Offered only while the link is near enough to its end to be worth renewing — the same "nearly up" hint the
    // row used to gate its Extend button on — and only when the server advertised the rel at all (ADR 0543).
    public bool CanRenew => Link.CanExtend && Link.AvailabilityHref is not null;

    [ObservableProperty] private int _days = 90;

    // Null means unlimited, which is also what the server takes: there is no "0 = unlimited" convention to learn.
    [ObservableProperty] private int? _maxAccesses;

    [ObservableProperty] private string _status = "";

    // True once a renewal succeeded, so the list behind this dialog knows to reload — the row's expiry, cap and
    // ETag all just changed, and a stale ETag would make its next action fail a precondition.
    public bool Renewed { get; private set; }

    public Action? RequestClose { get; set; }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Link.AvailabilityHref is not { } href)
        {
            return;
        }

        if (await _api.RenewExternalLinkAsync(href, Days, MaxAccesses, Link.Etag))
        {
            Renewed = true;
            RequestClose?.Invoke();
            return;
        }

        Status = Strings.Get("ExtLinkDisabled");
    }
}
