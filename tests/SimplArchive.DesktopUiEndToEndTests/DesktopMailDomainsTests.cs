using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// What a mail-domain row OFFERS (#667, ADR 0692) — decided by the rels the server advertised, not by the flags
// beside them.
//
// The two agree today, which is exactly why this is worth pinning: a row that read `Verified` to decide whether
// to show "verify" would keep working until the day the server withholds the rel for another reason — a caller
// without the routing right — and would then offer a button that answers 403.
public class DesktopMailDomainRowTests
{
    private static MailDomainRowViewModel Row(bool verified, string? verifyHref, string? removeHref) =>
        new()
        {
            Info = new AdminClient.MailDomainInfo(
                Guid.NewGuid(), "contoso.example", verified,
                verified ? null : "_simplarchive-challenge.contoso.example",
                verified ? null : "simplarchive-domain-verification=abc",
                verifyHref, removeHref),
        };

    [Fact]
    public void An_unverified_row_offers_verification_and_shows_the_challenge()
    {
        var row = Row(verified: false, "/verify", "/remove");

        Assert.True(row.CanVerify);
        Assert.True(row.CanRemove);
        Assert.True(row.ShowsChallenge);
        Assert.Equal("_simplarchive-challenge.contoso.example", row.ChallengeName);
    }

    [Fact]
    public void A_verified_row_offers_no_verification_and_no_challenge()
    {
        var row = Row(verified: true, verifyHref: null, removeHref: "/remove");

        // Nothing left to prove: the server withholds the rel, and the button follows the rel.
        Assert.False(row.CanVerify);
        Assert.False(row.ShowsChallenge);
        Assert.True(row.CanRemove);
    }

    [Fact]
    public void A_reader_who_may_not_manage_is_offered_nothing_even_while_it_is_unverified()
    {
        // The case the FLAG would get wrong: still unverified, so a Verified-based rule would offer "verify" —
        // and the server would refuse it. No rels, no buttons (ADR 0543).
        var row = Row(verified: false, verifyHref: null, removeHref: null);

        Assert.False(row.CanVerify);
        Assert.False(row.CanRemove);

        // The challenge is still SHOWN: seeing what the domain is waiting for is reading, not managing, and a
        // reader who can see the list can see why a domain is not working.
        Assert.True(row.ShowsChallenge);
    }
}

// The dialog against a real server: a domain is claimed, comes back unverified, and carries what has to be
// published. It cannot be verified here — that needs a TXT record in real DNS for a domain the test invented,
// which is what TenantMailDomainsTests covers against the stubbed resolver.
[Collection(UiCollection.Name)]
public class DesktopMailDomainsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopMailDomainsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Adding_a_domain_lists_it_unverified_with_its_challenge()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var domain = $"d{Guid.NewGuid():N}.example";

        var model = new MailDomainsViewModel(api);
        await model.LoadAsync();

        // The demo's own domain is already there, declared by configuration and verified on arrival — so this
        // also proves that path (ADR 0692) reaches the client.
        Assert.Contains(model.Domains, d => d.Verified);
        Assert.True(model.CanAdd);

        model.NewDomain = domain;
        await model.AddCommand.ExecuteAsync(null);

        var added = model.Domains.Single(d => d.Domain == domain);
        Assert.False(added.Verified);
        Assert.True(added.ShowsChallenge);
        Assert.Equal($"_simplarchive-challenge.{domain}", added.ChallengeName);
        Assert.StartsWith("simplarchive-domain-verification=", added.ChallengeValue, StringComparison.Ordinal);

        // The box is cleared on success, so a second add does not silently re-submit the first domain.
        Assert.Equal(string.Empty, model.NewDomain);
    }

    [Fact]
    public async Task A_refusal_arrives_as_a_sentence_rather_than_a_silent_no_op()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var model = new MailDomainsViewModel(api);
        await model.LoadAsync();
        var before = model.Domains.Count;

        model.NewDomain = "admin@example.com"; // the address, not the domain — the commonest mistake
        await model.AddCommand.ExecuteAsync(null);

        Assert.NotEqual(string.Empty, model.Status);
        Assert.Equal(before, model.Domains.Count);
    }
}
