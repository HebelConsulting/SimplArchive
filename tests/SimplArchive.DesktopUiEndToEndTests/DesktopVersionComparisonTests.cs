using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of version comparison (ADR "Document version comparison"): the real SimplArchiveApiClient
// lists a document's confirmed versions and produces an inline diff of two text versions.
[Collection(UiCollection.Name)]
public class DesktopVersionComparisonTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopVersionComparisonTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Lists_versions_and_diffs_two_text_versions()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"cmp-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("one\ntwo\nthree\n"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.UploadNewVersionAsync(doc.Id, Encoding.UTF8.GetBytes("one\nTWO edited\nthree\nfour\n"), ".txt");

        var versions = await api.GetVersionsAsync(doc.Href("versions"));
        Assert.Equal(2, versions.Count); // both confirmed, newest first
        Assert.Equal(2, versions[0].VersionNumber);

        // The listing carries the confirmed-version count that gates the "Compare versions" action (ADR
        // "Compare-versions gating + default") — 2 here, so the action is enabled.
        Assert.Equal(2, (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Id == doc.Id).VersionCount);

        // The version collection advertises ONE compare address; the pair travels as query parameters.
        var (_, compareHref) = await api.GetVersionsWithLinksAsync(doc.Href("versions"));
        var cmp = await api.GetVersionComparisonAsync(compareHref!, versions[1].Id, versions[0].Id);
        Assert.True(cmp.Available);
        Assert.Contains(cmp.Lines, l => l.Op == 0 && l.Text == "one");     // unchanged
        Assert.Contains(cmp.Lines, l => l.Op == 2 && l.Text == "two");     // removed
        Assert.Contains(cmp.Lines, l => l.Op == 1 && l.Text == "TWO edited"); // added
        Assert.Contains(cmp.Lines, l => l.Op == 1 && l.Text == "four");    // added

        // The dialog VM defaults the pickers to latest-vs-penultimate but does NOT run the diff (ADR "Explicit
        // compare", issue #371): the result area shows the hint and Compare is enabled, waiting for a click.
        var cvm = new CompareVersionsViewModel();
        await cvm.SetupAsync(api, doc.Id, "cmp", doc.Href("versions"));
        Assert.Equal(versions[0].Id, cvm.ToVersion!.Id);   // newest
        Assert.Equal(versions[1].Id, cvm.FromVersion!.Id); // penultimate
        Assert.Empty(cvm.Lines);                           // nothing compared yet
        Assert.True(cvm.ShowHint);
        Assert.True(cvm.CompareCommand.CanExecute(null));  // two different versions are selected

        // Clicking Compare is what runs it.
        await cvm.CompareCommand.ExecuteAsync(null);
        Assert.NotEmpty(cvm.Lines);
        Assert.False(cvm.ShowHint);

        // Changing a picker discards the diff and returns to the hint — a stale diff must never be attributed to
        // the new selection. Picking the SAME version on both sides also disables Compare.
        cvm.FromVersion = cvm.ToVersion;
        Assert.Empty(cvm.Lines);
        Assert.True(cvm.ShowHint);
        Assert.False(cvm.CompareCommand.CanExecute(null));
    }
}
