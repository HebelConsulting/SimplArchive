using System.IO.Compression;
using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The staging half of "drag documents out to the OS filesystem" (issue #266) over the real api client — the OS
// drop itself can't be exercised headlessly, so this verifies `DragOutStager` produces the right temp files: a
// document → its current-version file (<stem><ext>) with the exact bytes, a folder → a recursive .zip of its
// documents.
[Collection(UiCollection.Name)]
public class DesktopDragOutTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDragOutTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Stages_a_document_as_a_file_and_a_folder_as_a_zip()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");

        // A standalone document.
        var docName = $"dragout-{Guid.NewGuid():N}.txt";
        var docBytes = Encoding.UTF8.GetBytes($"doc-{Guid.NewGuid():N}");
        var docId = await api.Documents.UploadFileAsync(repo.Href("children"), docName, docBytes);
        var docStem = Path.GetFileNameWithoutExtension(docName); // Document.Name is a bare stem (ADR 0277)

        // A folder with one document inside.
        var folderName = $"dragfolder-{Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Href("children"), folderName);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == folderName);
        var childName = $"child-{Guid.NewGuid():N}.txt";
        var childBytes = Encoding.UTF8.GetBytes($"child-{Guid.NewGuid():N}");
        await api.Documents.UploadFileAsync(folder.Href("children"), childName, childBytes);
        var childStem = Path.GetFileNameWithoutExtension(childName);

        // Stage both for a drag-out — each item carries the addresses its ROW advertised (ADR 0555).
        var docRow = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Id == docId);
        var staged = await DragOutStager.StageAsync(api,
        [
            new DragOutItem(docStem, IsFolder: false, docRow.Href("versions"), null),
            new DragOutItem(folderName, IsFolder: true, null, folder.Href("children")),
        ]);
        Assert.Equal(2, staged.Count);
        var stagingDir = Path.GetDirectoryName(staged[0])!;

        try
        {
            // The document staged as "<stem>.txt" with its exact bytes.
            var docFile = staged.Single(p => p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(docStem + ".txt", Path.GetFileName(docFile));
            Assert.Equal(docBytes, await File.ReadAllBytesAsync(docFile));

            // The folder staged as "<name>.zip" containing the child document with its exact bytes.
            var zipFile = staged.Single(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(folderName + ".zip", Path.GetFileName(zipFile));
            using var zip = ZipFile.OpenRead(zipFile);
            var entry = zip.Entries.Single();
            Assert.Equal(childStem + ".txt", entry.FullName);
            await using var es = entry.Open();
            using var ms = new MemoryStream();
            await es.CopyToAsync(ms);
            Assert.Equal(childBytes, ms.ToArray());
        }
        finally
        {
            Directory.Delete(stagingDir, recursive: true);
        }
    }
}
