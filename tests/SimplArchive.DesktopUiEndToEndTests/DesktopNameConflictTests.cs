using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Filing a file whose name is already used in the target folder (ADR "A name conflict on filing is a question,
// not a refusal"). It used to return 409, put a message on the status line that the batch summary overwrote a
// moment later, and drop the file — so filing appeared to do nothing at all.
//
// This drives UploadConflictResolver directly, which is where the decision lives precisely so it can be
// exercised without a window: the OS drag and the modal itself cannot be produced headlessly, and the prompt is
// a callback for the same reason. What the view adds on top is one line constructing the dialog.
[Collection(UiCollection.Name)]
public class DesktopNameConflictTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopNameConflictTests(SelfHostedAppFixture app) => _app = app;

    private async Task<SimplArchiveApiClient> ApiAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        return new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
    }

    [Fact]
    public async Task Filing_over_a_taken_name_can_become_a_new_version_of_what_is_there()
    {
        var (api, _, childrenHref, fileName) = await ArrangeCollisionAsync();

        var prompts = new List<UploadConflictResolver.NameConflictRequest>();
        var filed = await new UploadConflictResolver(api).ResolveAsync(
            childrenHref, fileName, Encoding.UTF8.GetBytes("second revision"),
            req =>
            {
                prompts.Add(req);
                return Task.FromResult<UploadConflictResolver.NameConflictChoice?>(
                    new UploadConflictResolver.NameConflictChoice("version", "", "why this revision"));
            },
            _ => { });

        Assert.True(filed);

        // The prompt was told the collision is with a real document, so "as a new version" was offerable.
        Assert.True(Assert.Single(prompts).CanFileAsVersion);

        // One document, now two versions — not a second document, and not a silent no-op.
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var children = (await api.GetFolderContentsAsync(childrenHref)).Children;
        var row = Assert.Single(children, c => c.Name == stem);
        var versions = await api.GetVersionsAsync(row.Href("versions"));
        Assert.Equal(2, versions.Count);

        // The comment the user typed rides on the new version (ADR 0528) — it is the whole reason the dialog
        // asks for one, so a version filed without it would make the field decorative.
        Assert.Contains(versions, v => v.Comment == "why this revision");
    }

    [Fact]
    public async Task Filing_over_a_taken_name_can_become_a_new_document_beside_it()
    {
        var (api, _, childrenHref, fileName) = await ArrangeCollisionAsync();
        var stem = Path.GetFileNameWithoutExtension(fileName);

        string? suggested = null;
        var filed = await new UploadConflictResolver(api).ResolveAsync(
            childrenHref, fileName, Encoding.UTF8.GetBytes("a different document"),
            req =>
            {
                suggested = req.SuggestedName;
                return Task.FromResult<UploadConflictResolver.NameConflictChoice?>(
                    new UploadConflictResolver.NameConflictChoice("rename", req.SuggestedName, ""));
            },
            _ => { });

        Assert.True(filed);

        // The offered name is free in that folder, so accepting it leaves BOTH documents standing.
        Assert.Equal($"{stem} (2)", suggested);
        var children = (await api.GetFolderContentsAsync(childrenHref)).Children;
        Assert.Single(children, c => c.Name == stem);
        Assert.Single(children, c => c.Name == $"{stem} (2)");
    }

    [Fact]
    public async Task Cancelling_the_prompt_files_nothing()
    {
        var (api, _, childrenHref, fileName) = await ArrangeCollisionAsync();
        var stem = Path.GetFileNameWithoutExtension(fileName);

        var filed = await new UploadConflictResolver(api).ResolveAsync(
            childrenHref, fileName, Encoding.UTF8.GetBytes("should not be filed"),
            _ => Task.FromResult<UploadConflictResolver.NameConflictChoice?>(null),
            _ => { });

        Assert.False(filed);

        // Cancelling must not be a quiet version-bump either: the document is still on its first version.
        var children = (await api.GetFolderContentsAsync(childrenHref)).Children;
        var row = Assert.Single(children, c => c.Name == stem);
        Assert.Single(await api.GetVersionsAsync(row.Href("versions")));
    }

    [Fact]
    public async Task A_name_held_by_a_folder_cannot_be_answered_with_a_new_version()
    {
        var (api, folderId, childrenHref, _) = await ArrangeCollisionAsync();

        // Sibling names are unique across folders AND documents, so a file can collide with a FOLDER. Offering
        // "file it as a new version of that" would post a version to the folder and turn it into a document.
        var folderName = $"nc-sub-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(folderId, folderName);

        UploadConflictResolver.NameConflictRequest? prompt = null;
        var filed = await new UploadConflictResolver(api).ResolveAsync(
            childrenHref, folderName + ".txt", Encoding.UTF8.GetBytes("body"),
            req =>
            {
                prompt = req;
                return Task.FromResult<UploadConflictResolver.NameConflictChoice?>(
                    new UploadConflictResolver.NameConflictChoice("rename", req.SuggestedName, ""));
            },
            _ => { });

        Assert.True(filed);
        Assert.False(prompt!.CanFileAsVersion);

        // The folder is still a folder — it has no versions of its own — and the file landed beside it.
        var children = (await api.GetFolderContentsAsync(childrenHref)).Children;
        Assert.False(Assert.Single(children, c => c.Name == folderName).HasVersions);
        Assert.Single(children, c => c.Name == $"{folderName} (2)");
    }

    // A folder of its own holding one document, plus the file name that will collide with it. Its own folder per
    // test so the collision is unambiguous and concurrent tests cannot see each other's rows.
    private async Task<(SimplArchiveApiClient Api, Guid FolderId, string ChildrenHref, string FileName)> ArrangeCollisionAsync()
    {
        var api = await ApiAsync();
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");

        var folderName = $"nc-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, folderName);

        // The new folder's own children address comes from the row the repository listing now advertises
        // (ADR 0555) rather than being rebuilt from its id.
        var repoChildrenHref = (await api.GetDocumentLinksAsync(repo.Id))["children"];
        var folderRow = (await api.GetFolderContentsAsync(repoChildrenHref)).Children.Single(c => c.Name == folderName);
        var childrenHref = folderRow.Href("children");

        var fileName = $"invoice-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(childrenHref, fileName, Encoding.UTF8.GetBytes("first revision"));
        return (api, folderRow.Id, childrenHref, fileName);
    }
}
