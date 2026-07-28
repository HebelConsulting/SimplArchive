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

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var targetName = $"BulkTarget-{Guid.NewGuid():N}";
        await api.CreateFolderAsync(repo.Id, targetName);

        var prefix = $"bulk-{Guid.NewGuid():N}";
        for (var i = 0; i < 3; i++)
        {
            await api.UploadFileAsync(repo.Id, $"{prefix}-{i}.txt", Encoding.UTF8.GetBytes("x"));
        }
        var children = await api.GetChildrenAsync(repo.Id);
        var targetId = children.Single(n => n.Name == targetName).Id;
        var ids = children.Where(n => n.Name.StartsWith(prefix)).Select(n => n.Id).ToList();
        Assert.Equal(3, ids.Count);

        var tagged = await api.BulkAddTagsAsync(ids, ["Batch", "reviewed"]);
        Assert.Equal(3, tagged.Succeeded);
        foreach (var id in ids)
        {
            Assert.Equal(new[] { "batch", "reviewed" }, await api.GetTagsAsync(id));
        }

        var confidential = (await api.GetSensitivityLabelsAsync()).Items.Single(l => l.Name == "Confidential");
        var classified = await api.BulkSetSensitivityAsync(ids, confidential.Id);
        Assert.Equal(3, classified.Succeeded);
        Assert.Equal(confidential.Id, (await api.GetDocumentSensitivityAsync(ids[0])).LabelId);

        var moved = await api.BulkMoveAsync(ids.Take(2), targetId);
        Assert.Equal(2, moved.Succeeded);
        var movedInto = (await api.GetChildrenAsync(targetId)).Select(n => n.Id).ToHashSet();
        Assert.Contains(ids[0], movedInto);
        Assert.Contains(ids[1], movedInto);

        var deleted = await api.BulkDeleteAsync(ids);
        Assert.Equal(3, deleted.Succeeded);
    }
}
