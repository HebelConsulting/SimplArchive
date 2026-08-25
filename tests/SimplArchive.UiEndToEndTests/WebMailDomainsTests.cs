using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Registering a mail domain from the Tenant tab (#667, ADR 0692).
//
// The API shipped first and was unreachable from either client, which is the whole shape of this issue: mail
// ingress worked end to end from the day it shipped and there was no supported way to switch it on. So the
// test drives the dialog, not the endpoint — what it proves is that a person can get there.
//
// It cannot verify a domain: that needs a TXT record in real DNS for a domain the test invented. What it can
// prove is the half an administrator has to act on — that the challenge is shown, and shown in full.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebMailDomainsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMailDomainsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_domain_is_added_from_the_tenant_tab_and_shows_its_challenge()
    {
        var domain = $"d{Guid.NewGuid():N}.example";

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label='Tenant']").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Mail domains", Exact = true }).First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        await dialog.GetByLabel("Domain").FillAsync(domain);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();

        // Listed, and honest about what it is not yet: nothing is delivered for an unverified domain.
        await Expect(dialog.GetByText(domain).First).ToBeVisibleAsync();
        await Expect(dialog.GetByText("Not verified").First).ToBeVisibleAsync();

        // The challenge, in full. An administrator has to put BOTH halves into a DNS zone, so a UI that showed
        // only the value — or truncated the token — would send them to the server logs to find the rest.
        await Expect(dialog.GetByText($"_simplarchive-challenge.{domain}")).ToBeVisibleAsync();
        var value = await dialog.GetByText(new System.Text.RegularExpressions.Regex("simplarchive-domain-verification=")).First.InnerTextAsync();
        Assert.StartsWith("simplarchive-domain-verification=", value.Trim(), StringComparison.Ordinal);
        Assert.True(value.Trim().Length > "simplarchive-domain-verification=".Length + 20, $"the token looks truncated: '{value}'");
    }

    [Fact]
    public async Task Something_that_is_not_a_domain_is_refused_in_the_readers_language()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label='Tenant']").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Mail domains", Exact = true }).First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByLabel("Domain").FillAsync("admin@example.com");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();

        // The CLIENT's sentence for the server's error code, not the API's English `detail` (#423/#424) — the
        // guard NoServerDetailInClientsTests keeps that true, and this shows the reader actually gets words.
        await Expect(page.Locator(".mud-snackbar").GetByText("Enter the domain part on its own", new() { Exact = false }))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_verified_domain_offers_no_way_to_verify_it_again()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label='Tenant']").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Mail domains", Exact = true }).First.ClickAsync();

        // The demo's own domain, declared by configuration and therefore verified on arrival (ADR 0692) — so
        // this also proves that path reaches the UI.
        //
        // Located by the DOMAIN, not by the word "Verified": HasText is a case-insensitive SUBSTRING match, so
        // "Verified" also matches "Not verified" — and picking .First then lands on an unverified row, which
        // legitimately does have the button and fails the assertion for the opposite of the real reason.
        var demoDomain = SelfHostedAppFixture.AdminEmail.Split('@')[1];
        var dialog = page.Locator(".mud-dialog");
        var verifiedRow = dialog.Locator("tr", new() { HasText = demoDomain }).First;
        await Expect(verifiedRow).ToBeVisibleAsync();

        // No verify affordance, because the server advertises no `verify` rel once there is nothing to prove.
        // Asserted on the ROW rather than the dialog: an unverified row elsewhere in the table legitimately has
        // one, and a dialog-wide assertion would pass or fail on which other rows happen to exist.
        await Expect(verifiedRow.GetByRole(AriaRole.Button, new() { Name = "Check the DNS record now" }))
            .ToHaveCountAsync(0);
        await Expect(verifiedRow.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToHaveCountAsync(1);
    }
}
