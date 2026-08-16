using System.Security.Cryptography;
using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of duplicate detection (ADR "Duplicate document detection"): the real SimplArchiveApiClient
// finds a document whose content hash matches an uploaded file's, and reports none for unknown content.
[Collection(UiCollection.Name)]
public class DesktopDuplicateDetectionTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDuplicateDetectionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Finds_an_identical_document_by_content_hash()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var content = Encoding.UTF8.GetBytes($"desktop duplicate content {Guid.NewGuid():N}\n");
        var fileName = $"dupd-{Guid.NewGuid():N}.txt";
        await api.Documents.UploadFileAsync(repo.Id, fileName, content);
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));

        // The same content's hash finds the just-uploaded document (this is what the client computes pre-upload).
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var dups = await api.Documents.FindDuplicatesAsync(hash);
        Assert.Contains(dups, d => d.Id == doc.Id);
        Assert.All(dups, d => Assert.False(string.IsNullOrEmpty(d.Path)));

        // Unknown content → no duplicates.
        Assert.Empty(await api.Documents.FindDuplicatesAsync(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())))));
    }
}
