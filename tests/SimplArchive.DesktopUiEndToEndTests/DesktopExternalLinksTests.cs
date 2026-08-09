using System.Text;
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
        var folder = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == name);

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
    //
    // A DOCUMENT, deliberately. This test used to toggle the switch on a folder, which stopped proving anything
    // the day folders became unshareable: a folder has no rel with the switch either way, so "off → null" passed
    // for the wrong reason and "on → not null" simply failed. The folder case is now asserted separately, below,
    // where it says what it means.
    [Fact]
    public async Task The_share_affordance_appears_only_when_the_server_advertises_the_rel()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"rel-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("shareable"));
        var document = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        var before = await api.GetTenantSettingsAsync();
        try
        {
            await SetExternalLinksAsync(api, before, allow: false);
            Assert.Null((await api.GetDocumentDetailAsync(document.Id)).ExternalLinksHref);

            await SetExternalLinksAsync(api, before, allow: true);
            var href = (await api.GetDocumentDetailAsync(document.Id)).ExternalLinksHref;
            Assert.NotNull(href);
            Assert.Contains(document.Id.ToString(), href, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // The tenant is shared with every other test in this collection, so put the switch back however it
            // started rather than assuming it was off.
            await SetExternalLinksAsync(api, before, before.AllowExternalLinks);
        }
    }

    // A folder is not shareable — POST answers CANNOT_SHARE_FOLDER — so it must not advertise the rel either,
    // even with the tenant switch ON. Otherwise the client draws a share button whose only outcome is a refusal,
    // which is the shape ADR 0543 rules out. Asserted with the switch on, since off would pass either way.
    [Fact]
    public async Task A_folder_never_advertises_the_share_rel()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"nofolder-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, name);
        var folder = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == name);

        var before = await api.GetTenantSettingsAsync();
        try
        {
            await SetExternalLinksAsync(api, before, allow: true);
            Assert.Null((await api.GetDocumentDetailAsync(folder.Id)).ExternalLinksHref);
        }
        finally
        {
            await SetExternalLinksAsync(api, before, before.AllowExternalLinks);
        }
    }

    // Everything except the one flag is carried through unchanged: the endpoint is a full replacement, so
    // omitting a field here would quietly rewrite the shared tenant for every other test in this collection.
    private static Task SetExternalLinksAsync(SimplArchiveApiClient api, SimplArchiveApiClient.TenantSettingsInfo before, bool allow) =>
        api.SetTenantSettingsAsync(before.Name, before.DefaultOcrLanguages, before.AuditRetentionDays, before.CheckoutTtlDays,
            before.CheckoutWarningDays, before.WormLockMode, before.RequireMfa, before.AllowPasskeyLogin,
            before.RequireDispositionReview, before.RestrictTagsToCatalog, before.EnforceClearance,
            allow, before.ExternalLinkMaxDays, before.ExternalLinkDefaultAccesses, before.ShowExternalLinkUrl,
            before.StorageQuotaBytes, before.IncompleteUploadCleanupDays, null, null);
}
