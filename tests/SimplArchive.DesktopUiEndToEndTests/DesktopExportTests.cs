using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Repository export (ADR "Repository export") via the real desktop api client: the demo admin (a tenant admin)
// creates a repository with a document, exports it to a .zip, and the archive's manifest + content-addressed
// blob round-trip byte-for-byte — all through the real SimplArchiveApiClient against the running Api.
[Collection(UiCollection.Name)]
public class DesktopExportTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopExportTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Export_a_repository_to_a_zip_and_the_blob_round_trips()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repoName = $"Desktop export {Guid.NewGuid():N}";
        await api.Documents.CreateRepositoryAsync(repoName);
        var repoId = (await api.Documents.GetRepositoriesAsync()).Single(r => r.Name == repoName).Id;

        var payload = Encoding.UTF8.GetBytes($"desktop-export-{Guid.NewGuid():N}");
        await api.Documents.UploadFileAsync(repoId, "report.txt", payload);

        var options = new DocumentsClient.RepositoryExportOptions(ActiveOnly: false, null, null, null, null, null);
        var zipBytes = await api.Documents.ExportRepositoryAsync(repoId, options);

        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

        using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync()).RootElement;
        Assert.Equal(2, manifest.GetProperty("formatVersion").GetInt32()); // 2 since the chat rename (#382)
        Assert.Equal(1, manifest.GetProperty("counts").GetProperty("versions").GetInt32());

        var blob = Assert.Single(archive.Entries, e => e.FullName.StartsWith("blobs/"));
        using var blobStream = new MemoryStream();
        await using (var open = blob.Open())
        {
            await open.CopyToAsync(blobStream);
        }

        Assert.Equal(payload, blobStream.ToArray());
    }
}
