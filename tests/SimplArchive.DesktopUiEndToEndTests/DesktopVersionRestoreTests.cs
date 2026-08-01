using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of version restore (ADR "Version-restore via a current-version pointer", issue #265): the real
// SimplArchiveApiClient makes an older version current via the pointer — no new version is created, the older
// version is flagged current, and its content is served as current.
[Collection(UiCollection.Name)]
public class DesktopVersionRestoreTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopVersionRestoreTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Make_current_pins_the_old_version_without_creating_a_new_one()
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
        Assert.True(versions.Single(v => v.VersionNumber == 2).IsCurrent); // v2 is current before the roll-back
        var v1 = versions.Single(v => v.VersionNumber == 1);

        // Make v1 current → still exactly two versions, and v1 is now flagged current (no copy).
        await api.RestoreVersionAsync(doc.Id, v1.Id);
        var after = await api.GetVersionsAsync(doc.Id);
        Assert.Equal(2, after.Count);
        var current = after.Single(v => v.IsCurrent);
        Assert.Equal(v1.Id, current.Id);

        // Its content (served as current) equals version A.
        var bytes = await api.DownloadVersionBytesAsync(current.DownloadUrl!);
        Assert.Equal(contentA, Encoding.UTF8.GetString(bytes));

        // Uploading a new version takes over as current.
        await api.UploadNewVersionAsync(doc.Id, Encoding.UTF8.GetBytes($"C-{Guid.NewGuid():N}"), ".txt");
        var final = await api.GetVersionsAsync(doc.Id);
        Assert.Equal(3, final.Count);
        Assert.Equal(3, final.Single(v => v.IsCurrent).VersionNumber);
    }
}
