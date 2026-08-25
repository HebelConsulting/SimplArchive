using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One registered mail domain, and what it still needs.</summary>
public sealed partial class MailDomainRowViewModel : ObservableObject
{
    public required AdminClient.MailDomainInfo Info { get; init; }

    public string Domain => Info.Domain;

    public bool Verified => Info.Verified;

    public string StatusText => Info.Verified ? Strings.Get("MdVerified") : Strings.Get("MdUnverified");

    /// <summary>
    /// Whether there is anything to verify — asked of the advertised REL, not of the flag beside it.
    /// </summary>
    /// <remarks>
    /// Both would answer the same today. The rel is the one that keeps answering correctly when it stops being
    /// the same question — a caller without the routing right gets no rel and no button, while the flag would
    /// still say "unverified" and offer one the server refuses (ADR 0543).
    /// </remarks>
    public bool CanVerify => Info.VerifyHref is not null;

    public bool CanRemove => Info.RemoveHref is not null;

    /// <summary>The challenge is shown only while it is still the thing to do.</summary>
    public bool ShowsChallenge => !Info.Verified && Info.ChallengeValue is { Length: > 0 };

    public string ChallengeName => Info.ChallengeName ?? string.Empty;

    public string ChallengeValue => Info.ChallengeValue ?? string.Empty;
}

/// <summary>
/// The tenant's mail domains (#667, ADR 0692) — the desktop twin of the web dialog, which is the same surface
/// (ADR 0511): list, add, verify by DNS challenge, remove.
/// </summary>
public sealed partial class MailDomainsViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;

    public MailDomainsViewModel(SimplArchiveApiClient api) => _api = api;

    public ObservableCollection<MailDomainRowViewModel> Domains { get; } = [];

    [ObservableProperty] private string _newDomain = string.Empty;
    [ObservableProperty] private bool _canManage;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Where a new claim is POSTed, as the collection advertised it. Null → no add affordance.</summary>
    private string? _addHref;

    /// <summary>
    /// Whether a claim may be added — asked of the advertised REL, never of <see cref="CanManage"/>.
    /// </summary>
    /// <remarks>
    /// The two agree by construction today, and the rel is the one that keeps agreeing: it is the server's
    /// answer to "may you do this, here, now" (ADR 0543). Falling back to the flag was written here for a
    /// moment to make a SCREENSHOT show the add row, which is the wrong direction entirely — the fixture sets
    /// the address instead.
    /// </remarks>
    public bool CanAdd => _addHref is not null;

    public bool IsEmpty => Domains.Count == 0;

    /// <summary>
    /// A dialog filled with synthetic rows for the headless screenshot (`--maildomains-screenshot`).
    /// </summary>
    /// <remarks>
    /// A screenshot run reaches no server, so without this the dialog would be photographed empty and the
    /// capture would verify nothing. Both states on purpose: the unverified row carries the challenge an
    /// administrator must publish, and the verified one advertises no `verify` — which is what proves the
    /// buttons follow the server's rels rather than a local flag.
    /// </remarks>
    public static MailDomainsViewModel ForScreenshot()
    {
        // A PLACEHOLDER, not a path: the screenshot never issues a request, and only nullness is read. An
        // address-shaped literal here would count as a composed URL in client code, which
        // ClientHypermediaTests refuses — correctly, since "it is only for a screenshot" is how that rule
        // would start eroding. (Its matcher looks for a quote followed by the api path prefix, so a comment
        // SPELLING that prefix out trips it too — this one is worded around it, deliberately.)
        var model = new MailDomainsViewModel(null!) { CanManage = true, _addHref = "(screenshot)" };
        model.Domains.Add(new MailDomainRowViewModel
        {
            Info = new AdminClient.MailDomainInfo(
                Guid.NewGuid(), "contoso.example", Verified: false,
                "_simplarchive-challenge.contoso.example",
                "simplarchive-domain-verification=qmny5W0JLnkcGKt98oEb1zmCqQ0",
                VerifyHref: "/verify", RemoveHref: "/remove"),
        });
        model.Domains.Add(new MailDomainRowViewModel
        {
            Info = new AdminClient.MailDomainInfo(
                Guid.NewGuid(), "contoso.de", Verified: true, null, null, VerifyHref: null, RemoveHref: "/remove"),
        });

        return model;
    }

    public async Task LoadAsync()
    {
        try
        {
            var list = await _api.Admin.GetMailDomainsAsync();
            Domains.Clear();
            foreach (var info in list.Domains)
            {
                Domains.Add(new MailDomainRowViewModel { Info = info });
            }

            CanManage = list.CanManage;
            _addHref = list.AddHref;
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (_addHref is not { } href || string.IsNullOrWhiteSpace(NewDomain))
        {
            return;
        }

        await RunAsync(() => _api.Admin.AddMailDomainAsync(href, NewDomain.Trim()), () => NewDomain = string.Empty);
    }

    [RelayCommand]
    private async Task VerifyAsync(MailDomainRowViewModel? row)
    {
        if (row?.Info.VerifyHref is not { } href)
        {
            return;
        }

        await RunAsync(
            () => _api.Admin.VerifyMailDomainAsync(href),
            () => Status = string.Format(CultureInfo.CurrentCulture, Strings.Get("MdVerifiedToast"), row.Domain));
    }

    [RelayCommand]
    private async Task RemoveAsync(MailDomainRowViewModel? row)
    {
        if (row?.Info.RemoveHref is not { } href)
        {
            return;
        }

        await RunAsync(
            () => _api.Admin.RemoveMailDomainAsync(href),
            // Says what it COST rather than that a row went away: removing a domain stops mail arriving for
            // everyone at it, immediately, and that is the part worth putting in front of the person.
            () => Status = string.Format(CultureInfo.CurrentCulture, Strings.Get("MdRemovedToast"), row.Domain));
    }

    /// <summary>
    /// Runs one write, then re-reads the list — so what is on screen is what the server holds, including the
    /// rels, which is what decides which buttons a row offers.
    /// </summary>
    /// <remarks>
    /// The refusal is surfaced verbatim from <see cref="ApiActionException"/>, which carries the LOCALIZED
    /// sentence for the server's error code (#423/#424) — never the API's English detail.
    /// </remarks>
    private async Task RunAsync(Func<Task> write, Action onSuccess)
    {
        Status = string.Empty;
        try
        {
            await write();
            onSuccess();
        }
        catch (Exception e)
        {
            Status = e.Message;
        }

        await LoadAsync();
    }
}
