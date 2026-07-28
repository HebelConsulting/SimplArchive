using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of version restore (ADR "Version restore"): the real SimplArchiveApiClient rolls back to an
// older version, and the new current version's content equals that older version's.
[Collection(UiCollection.Name)]
public class DesktopVersionRestoreTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopVersionRestoreTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Restore_makes_the_old_content_current()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"restore-{Guid.NewGuid():N}.txt";
        var contentA = $"A-{Guid.NewGuid():N}";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes(contentA));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        // A second version of the same document, with different content.
        await api.UploadNewVersionAsync(doc.Id, Encoding.UTF8.GetBytes($"B-{Guid.NewGuid():N}"), ".txt");

        var versions = await api.GetVersionsAsync(doc.Id);
        Assert.Equal(2, versions.Count);
        var v1 = versions.Single(v => v.VersionNumber == 1);

        // Restore v1 → a v3 appears whose content is v1's (content A).
        await api.RestoreVersionAsync(doc.Id, v1.Id);
        var after = await api.GetVersionsAsync(doc.Id);
        Assert.Equal(3, after.Count);
        var newest = after.OrderByDescending(v => v.VersionNumber ?? 0).First();
        var bytes = await api.DownloadVersionBytesAsync(newest.DownloadUrl!);
        Assert.Equal(contentA, Encoding.UTF8.GetString(bytes));
    }
}
