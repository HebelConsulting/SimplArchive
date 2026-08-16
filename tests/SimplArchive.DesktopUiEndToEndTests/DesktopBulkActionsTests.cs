using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of bulk actions (ADR "Bulk actions on selected documents"): the real SimplArchiveApiClient
// adds tags, sets sensitivity, moves, and deletes a set of documents in one call each, reporting succeeded vs
// skipped.
[Collection(UiCollection.Name)]
public class DesktopBulkActionsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopBulkActionsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Bulk_tag_classify_move_and_delete()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var targetName = $"BulkTarget-{Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Id, targetName);

        var prefix = $"bulk-{Guid.NewGuid():N}";
        for (var i = 0; i < 3; i++)
        {
            await api.Documents.UploadFileAsync(repo.Id, $"{prefix}-{i}.txt", Encoding.UTF8.GetBytes("x"));
        }
        var children = await api.Documents.GetChildrenAsync(repo.Href("children"));
        var targetId = children.Single(n => n.Name == targetName).Id;
        var ids = children.Where(n => n.Name.StartsWith(prefix)).Select(n => n.Id).ToList();
        Assert.Equal(3, ids.Count);

        var tagged = await api.Documents.BulkAddTagsAsync(ids, ["Batch", "reviewed"]);
        Assert.Equal(3, tagged.Succeeded);
        foreach (var id in ids)
        {
            Assert.Equal(new[] { "batch", "reviewed" }, await api.Documents.GetTagsAsync((await api.Documents.GetDocumentDetailAsync(id)).Href("tags")));
        }

        var confidential = (await api.Admin.GetSensitivityLabelsAsync()).Items.Single(l => l.Name == "Confidential");
        var classified = await api.Documents.BulkSetSensitivityAsync(ids, confidential.Id);
        Assert.Equal(3, classified.Succeeded);
        Assert.Equal(confidential.Id, (await api.Documents.GetDocumentSensitivityAsync(ids[0])).LabelId);

        var moved = await api.Documents.BulkMoveAsync(ids.Take(2), targetId);
        Assert.Equal(2, moved.Succeeded);
        var movedInto = (await api.Documents.GetChildrenAsync(targetId)).Select(n => n.Id).ToHashSet();
        Assert.Contains(ids[0], movedInto);
        Assert.Contains(ids[1], movedInto);

        var deleted = await api.Documents.BulkDeleteAsync(ids);
        Assert.Equal(3, deleted.Succeeded);
    }

    // The desktop drag-drop "Reference" action places shortcuts for the whole dragged set in one call
    // (ADR "Desktop drag-and-drop move and reference"); a repeat drop is idempotent (no duplicate shortcuts).
    [Fact]
    public async Task Bulk_reference_places_shortcuts_for_the_whole_set()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var targetName = $"RefTarget-{Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Id, targetName);

        var prefix = $"ref-{Guid.NewGuid():N}";
        for (var i = 0; i < 2; i++)
        {
            await api.Documents.UploadFileAsync(repo.Id, $"{prefix}-{i}.txt", Encoding.UTF8.GetBytes("x"));
        }
        var children = await api.Documents.GetChildrenAsync(repo.Href("children"));
        var targetId = children.Single(n => n.Name == targetName).Id;
        var ids = children.Where(n => n.Name.StartsWith(prefix)).Select(n => n.Id).ToList();
        Assert.Equal(2, ids.Count);

        var referenced = await api.Documents.BulkReferenceAsync(ids, targetId);
        Assert.Equal(2, referenced.Succeeded);

        // The originals stay put, and the target now holds a shortcut to each.
        var stillHome = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Select(n => n.Id).ToHashSet();
        Assert.Contains(ids[0], stillHome);
        Assert.Contains(ids[1], stillHome);
        var refs = (await api.Documents.GetReferencesAsync(targetId)).Select(r => r.TargetId).ToHashSet();
        Assert.Contains(ids[0], refs);
        Assert.Contains(ids[1], refs);

        // Idempotent: dropping the same set again places no duplicates.
        var again = await api.Documents.BulkReferenceAsync(ids, targetId);
        Assert.Equal(0, again.Succeeded);
        Assert.Equal(2, again.Skipped);
    }
}
