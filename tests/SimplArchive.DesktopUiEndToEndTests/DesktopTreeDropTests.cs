using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Dropping onto the Personal ▸ Inbox and Personal ▸ Check-out tree launchers (#467), over the real api client.
//
// The OS drag itself cannot be exercised headlessly, so this drives DropFiling — which is where the work lives
// precisely so it can be tested without a window. What the handler adds on top is routing (which launcher, which
// tab to open), and that is a switch over PersonalKind.
[Collection(UiCollection.Name)]
public class DesktopTreeDropTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeDropTests(SelfHostedAppFixture app) => _app = app;

    private async Task<SimplArchiveApiClient> ApiAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        return new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
    }

    [Fact]
    public async Task A_document_dropped_on_the_inbox_arrives_as_a_template_with_its_index_data()
    {
        var api = await ApiAsync();
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");

        var docName = $"tpl-{Guid.NewGuid():N}.txt";
        var docId = await api.UploadFileAsync(repo.Id, docName, Encoding.UTF8.GetBytes("template body"));

        var messages = new List<string>();
        var copied = await new DropFiling(api).CopyToInboxAsync([docId], messages.Add);

        Assert.Equal(1, copied);

        // It lands under the document's name plus the version's extension — the naming that later lets it be
        // dragged onto Check-out and matched back by filename.
        var stem = Path.GetFileNameWithoutExtension(docName);
        var item = (await api.Inbox.ListAsync()).Items.SingleOrDefault(i => i.Name == stem + ".txt");
        Assert.NotNull(item);

        // The point of a template is the staged mask, not the bytes: a copy that arrived without it would look
        // identical in a listing and force the user to re-enter everything.
        Assert.True(item!.HasMask);
    }

    [Fact]
    public async Task A_second_copy_of_the_same_document_is_reported_rather_than_overwriting()
    {
        var api = await ApiAsync();
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var docId = await api.UploadFileAsync(repo.Id, $"dup-{Guid.NewGuid():N}.txt", Encoding.UTF8.GetBytes("x"));

        var filing = new DropFiling(api);
        var messages = new List<string>();
        Assert.Equal(1, await filing.CopyToInboxAsync([docId], messages.Add));

        // The inbox is addressed BY NAME, so a silent second copy would destroy the first item's staged edits.
        messages.Clear();
        Assert.Equal(0, await filing.CopyToInboxAsync([docId], messages.Add));
        Assert.Contains(messages, m => m.Contains("could not be copied", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_file_naming_nothing_checked_out_is_refused_out_loud()
    {
        var api = await ApiAsync();

        // A NON-EMPTY checkout list whose names do not match, so this exercises the matching itself. An earlier
        // version passed an empty list, which would have passed even with the predicate replaced by `true` —
        // a test that cannot fail for the reason it names is not testing that reason.
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var otherId = await api.UploadFileAsync(repo.Id, $"other-{Guid.NewGuid():N}.txt", Encoding.UTF8.GetBytes("v1"));
        await api.CheckOutAsync(otherId);
        var checkouts = await api.GetCheckoutsAsync();
        Assert.NotEmpty(checkouts);

        // The drop must SAY it did nothing. Silence here is exactly how the reminder bug (#420) stayed hidden
        // for months: the user acts, nothing happens, and they conclude the feature is broken.
        var messages = new List<string>();
        var stashed = await new DropFiling(api).StashAsync(
            [($"names-nothing-{Guid.NewGuid():N}.txt", Encoding.UTF8.GetBytes("edited"))],
            checkouts,
            messages.Add);

        Assert.Equal(0, stashed);
        Assert.Contains(messages, m => m.Contains("does not match a document you have checked out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_edited_file_named_for_a_checked_out_document_becomes_its_working_copy()
    {
        var api = await ApiAsync();
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");

        var name = $"stash-{Guid.NewGuid():N}.txt";
        var docId = await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("v1"));
        await api.CheckOutAsync(docId);

        var checkouts = await api.GetCheckoutsAsync();
        var mine = checkouts.Single(c => c.Id == docId);

        // The round trip: downloaded, edited offline, dragged back. The FILENAME is what says which document it
        // belongs to, which is why the match is on Name + FileExtension rather than on anything the drop carries.
        var messages = new List<string>();
        var stashed = await new DropFiling(api).StashAsync(
            [(mine.Name + mine.FileExtension, Encoding.UTF8.GetBytes("edited offline"))],
            checkouts,
            messages.Add);

        Assert.Equal(1, stashed);
        Assert.True((await api.GetCheckoutsAsync()).Single(c => c.Id == docId).HasStash);
    }
}
