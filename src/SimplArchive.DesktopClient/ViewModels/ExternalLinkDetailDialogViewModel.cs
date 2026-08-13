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
// There is deliberately no other editing: a link's document and creator are settled when it is created, and
// renewing is the only live decision left.
//
// The URL is the exception, and this dialog used to state the opposite as if it were law ("the server hands it
// out exactly once, so this dialog cannot show it"). That stopped being true when issue #412 added the tenant's
// opt-in: the server will re-issue an existing link's URL, and says so by advertising `reveal-url`. The web
// client was taught this; the desktop was not, so it went on claiming the URL was unavailable on a tenant that
// had switched revealing ON — which is what a reader sees as "the link is not shown although the right is set".
public sealed partial class ExternalLinkDetailDialogViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;

    /// <param name="documentName">
    /// The document this link shares. Passed IN rather than read off the row: the per-document listing leaves
    /// documentName empty (the caller is already sitting on that document), so a dialog that trusted the row
    /// showed a blank "Document" — visible in the bug report this fixes. The cross-document list does fill it,
    /// so the row is used when the caller has nothing better.
    /// </param>
    public ExternalLinkDetailDialogViewModel(
        SimplArchiveApiClient api, SimplArchiveApiClient.ExternalLinkInfo link, string? documentName = null)
    {
        _api = api;
        Link = link;
        MaxAccesses = link.MaxAccesses;
        DocumentName = string.IsNullOrWhiteSpace(documentName) ? link.DocumentName : documentName;
    }

    public SimplArchiveApiClient.ExternalLinkInfo Link { get; }

    public string DocumentName { get; }

    public string CreatedByName => Link.CreatedByName;

    public string Expires => Link.ExpiresLocal;

    public string Accesses => $"{Link.AccessCount} / {Link.MaxAccesses?.ToString() ?? Strings.Get("ExtLinkUnlimited")}";

    // The URL, once revealed. Until then the row's `reveal-url` rel decides which of the two states this is:
    // a button to ask for it, or the honest note that this tenant does not hand it out (ADR 0543 — a missing
    // rel means "not available to you, here, now", and the wording beside it has to agree).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRevealedUrl))]
    [NotifyPropertyChangedFor(nameof(ShowRevealButton))]
    private string? _revealedUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRevealButton))]
    private bool _revealing;

    public bool CanReveal => Link.RevealUrlHref is not null;

    public bool HasRevealedUrl => !string.IsNullOrEmpty(RevealedUrl);

    public bool ShowRevealButton => CanReveal && !HasRevealedUrl && !Revealing;

    // Shown only where the tenant has NOT opted in — otherwise it would sit next to the URL contradicting it.
    public bool ShowUrlNote => !CanReveal;

    public string UrlNote => Strings.Get("ExtLinkUrlNotShown");

    /// <summary>Asks the server for this one link's URL, by following the rel the row advertised.</summary>
    [RelayCommand]
    private async Task RevealUrlAsync()
    {
        if (Link.RevealUrlHref is not { } href || Revealing)
        {
            return;
        }

        Revealing = true;
        try
        {
            RevealedUrl = await _api.RevealExternalLinkUrlAsync(href);
            if (RevealedUrl is null)
            {
                Status = Strings.Get("ExtLinkUrlRevealFailed");
            }
        }
        finally
        {
            Revealing = false;
        }
    }

    /// <summary>Copies the revealed URL — the point of revealing it is to paste it somewhere.</summary>
    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        if (RevealedUrl is { Length: > 0 } url && CopyToClipboard is { } copy)
        {
            await copy(url);
            Status = Strings.Get("ExtLinkUrlCopied");
        }
    }

    /// <summary>Supplied by the view — the VM stays toolkit-agnostic, as StatusReporter does elsewhere.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

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
