using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The demo seed's identities are deterministic END TO END (#781): not merely that DemoId is a pure function
// (DemoIdTests pins that with goldens), but that the real provisioning + seeding pipeline actually PLACED the
// demo content at those addresses. This is what the golden unit tests cannot see — an idFor left unwired at
// one call site still mints a fresh GUID, passes every unit test, and churns the kiosk's identities nightly,
// invisibly in any single run. Asserting the LIVE seeded app's ids against the derivation catches exactly the
// unwired site.
//
// Against the demo-seeded self-hosted app (the same seed the kiosk ships; tenant name "Demo" from
// SelfHostedApp). The expected ids are GOLDEN LITERALS from the reference RFC 4122 v5 implementation rather
// than calls into DemoId — this project's Api reference is build-order-only (ADR 0236), and a literal is the
// stronger pin anyway: it cannot drift together with the code it checks. If a slug changes deliberately, the
// kiosk's next reseed breaks every client cache — so a failure here is a decision point, not a stale literal.
[Collection(UiCollection.Name)]
public class DesktopDemoIdentityTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDemoIdentityTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_seeded_archive_lives_at_its_deterministic_addresses()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        // The repository — provisioned by TenantProvisioningService with the seeder's idFor.
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(r => r.Name == "Demo Repository");
        Assert.Equal(Guid.Parse("7e5f02af-67e7-51f0-9482-e0882d3e54bc"), repo.Id);

        // A folder (explicit slug) and a document (resource-stem slug) from the rich tree.
        async Task<Node> ChildAsync(Node parent, string name) =>
            (await api.Documents.GetChildrenAsync(parent.Href("children"))).Single(n => n.Name == name);

        var contracts = await ChildAsync(repo, "Contracts");
        Assert.Equal(Guid.Parse("6b6de497-5cc3-5c9c-b5f7-79a156418d4b"), contracts.Id);
        var acme = await ChildAsync(contracts, "Acme Corp");
        Assert.Equal(Guid.Parse("39029ca2-4943-5a55-8674-a93aa8cc9033"), acme.Id);
        var invoice = await ChildAsync(acme, "Invoice 2026-003");
        Assert.Equal(Guid.Parse("b0a55acd-bd88-5446-a194-8526b6ea6a35"), invoice.Id);

        // The personal space the phone-visible DAV collections live in — provisioned through the
        // personal-space idFor chain (ProvisionAsync for the space, the seeder for the mailbox).
        var personal = await api.Profile.GetPersonalRepositoryAsync();
        Assert.NotNull(personal);
        Assert.Equal(Guid.Parse("e2c076df-7267-57c2-abd9-6557b63e8bbc"), personal!.Id);
        var addressbook = await ChildAsync(personal, "My Addressbook");
        Assert.Equal(Guid.Parse("789ad67f-843c-5817-aa8a-d404777e0cff"), addressbook.Id);

        // …and the volmet contacts inside it, which is precisely what a phone's CardDAV client caches.
        var geneva = await ChildAsync(addressbook, "VOLMET Geneva");
        Assert.Equal(Guid.Parse("7a8e51d5-2d29-565b-ad50-44f8ad992802"), geneva.Id);

        // The mailbox the seeder now materializes eagerly, standing folders included.
        var mailbox = await ChildAsync(personal!, "My Mailbox");
        Assert.Equal(Guid.Parse("fb374472-0da3-5ac2-9c92-a0ab1d44eeb4"), mailbox.Id);
        var inbox = await ChildAsync(mailbox, "Inbox");
        Assert.Equal(Guid.Parse("87808af3-9f3a-5f3a-b62e-9bab8466703c"), inbox.Id);
    }
}
