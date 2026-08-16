using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The Check-out tab's detail panes (ADR "The Check-out tab shows what you are about to check in").
//
// The tab used to be a bare table. To see what you had actually edited you left it, found the document in
// Repositories, and looked at the ARCHIVED version — the one thing that is definitely not your edit. So the
// pane that matters is the preview, and what it must show is the WORKING COPY.
[Collection(UiCollection.Name)]
public class DesktopCheckoutDetailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCheckoutDetailTests(SelfHostedAppFixture app) => _app = app;

    private async Task<SimplArchiveApiClient> ApiAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        return new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
    }

    [Fact]
    public async Task The_preview_is_of_the_working_copy_and_follows_each_save()
    {
        var api = await ApiAsync();
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var docId = await api.UploadFileAsync(repo.Id, $"wc-{Guid.NewGuid():N}.txt", Encoding.UTF8.GetBytes("ARCHIVED BODY"));
        await api.Checkout.CheckOutViaDocumentAsync(await TestRels.DocumentSelfAsync(api, repo, docId));

        // Nothing saved yet: the row advertises no `preview` rel, and asking anyway yields nothing rather than
        // falling back to the archived version — which would be the wrong document shown confidently.
        var before = (await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == docId);
        Assert.Null(before.Href("preview"));
        Assert.Null(await api.Checkout.GetCheckoutPreviewAsync(before));

        await SaveWorkingCopyAsync(api, docId, "FIRST EDIT");
        var first = (await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == docId);
        Assert.NotNull(first.Href("preview"));
        Assert.Contains("FIRST EDIT", await FetchAsync(await api.Checkout.GetCheckoutPreviewAsync(first)));

        // Saving again must move the preview with it. The rendition cache is keyed on the source PATH and the
        // stash is rewritten under a stable key, so this is the case that silently served a stale picture.
        await SaveWorkingCopyAsync(api, docId, "SECOND EDIT");
        var second = (await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == docId);
        var body = await FetchAsync(await api.Checkout.GetCheckoutPreviewAsync(second));
        Assert.Contains("SECOND EDIT", body);
        Assert.DoesNotContain("FIRST EDIT", body);

        await api.Checkout.CheckInViaDocumentAsync(await TestRels.DocumentSelfAsync(api, repo, docId)); // releases the lock (DELETE checkout), leaving the fixture clean
    }

    private static async Task<string> FetchAsync(SimplArchiveApiClient.Preview? preview)
    {
        Assert.NotNull(preview);
        using var anonymous = new HttpClient();
        return await anonymous.GetStringAsync(preview!.PreviewUrl);
    }

    private static async Task SaveWorkingCopyAsync(SimplArchiveApiClient api, Guid docId, string content)
    {
        var checkout = (await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == docId);
        await api.Checkout.SaveWorkingCopyAsync(checkout, Encoding.UTF8.GetBytes(content));
    }
}
