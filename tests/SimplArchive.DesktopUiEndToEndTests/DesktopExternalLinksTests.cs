using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of external links (ADR 0546, issue #385). The dialog and its view-model already existed —
// built alongside the cross-document view — but nothing could reach the PER-DOCUMENT one: its href is advertised
// on the document resource, and this client read that resource twice (once for a name, once for a sensitivity
// label) without ever keeping the links. One read now serves all three.
[Collection(UiCollection.Name)]
public class DesktopExternalLinksTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopExternalLinksTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task One_read_of_the_document_carries_its_name_label_and_share_link()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"share-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, name);
        var folder = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == name);

        var detail = await api.GetDocumentDetailAsync(folder.Id);

        Assert.Equal(name, detail.Name);
        Assert.NotNull(detail.Sensitivity);

        // The two older accessors are now views onto the same read, so they must keep agreeing with it — that
        // equivalence is the whole reason it was safe to collapse them.
        Assert.Equal(detail.Name, await api.GetDocumentNameAsync(folder.Id));
        Assert.Equal(detail.Sensitivity.LabelId, (await api.GetDocumentSensitivityAsync(folder.Id)).LabelId);
    }

    // The affordance is driven ENTIRELY by whether the server advertised the rel — never by the client assuming a
    // URL. Toggling the tenant switch is the honest way to prove that: off → no rel → no share button; on → the
    // rel appears and is the href the dialog follows.
    [Fact]
    public async Task The_share_affordance_appears_only_when_the_server_advertises_the_rel()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"rel-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, name);
        var folder = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == name);

        var before = await api.GetTenantSettingsAsync();
        try
        {
            await SetExternalLinksAsync(api, before, allow: false);
            Assert.Null((await api.GetDocumentDetailAsync(folder.Id)).ExternalLinksHref);

            await SetExternalLinksAsync(api, before, allow: true);
            var href = (await api.GetDocumentDetailAsync(folder.Id)).ExternalLinksHref;
            Assert.NotNull(href);
            Assert.Contains(folder.Id.ToString(), href, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // The tenant is shared with every other test in this collection, so put the switch back however it
            // started rather than assuming it was off.
            await SetExternalLinksAsync(api, before, before.AllowExternalLinks);
        }
    }

    // Everything except the one flag is carried through unchanged: the endpoint is a full replacement, so
    // omitting a field here would quietly rewrite the shared tenant for every other test in this collection.
    private static Task SetExternalLinksAsync(SimplArchiveApiClient api, SimplArchiveApiClient.TenantSettingsInfo before, bool allow) =>
        api.SetTenantSettingsAsync(before.Name, before.DefaultOcrLanguages, before.AuditRetentionDays, before.CheckoutTtlDays,
            before.CheckoutWarningDays, before.WormLockMode, before.RequireMfa, before.AllowPasskeyLogin,
            before.RequireDispositionReview, before.RestrictTagsToCatalog, before.EnforceClearance,
            allow, before.ExternalLinkMaxDays, before.ExternalLinkDefaultAccesses,
            before.StorageQuotaBytes, before.IncompleteUploadCleanupDays, null, null);
}
