using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of configurable sensitivity labels (ADR "Configurable sensitivity labels + upload defaults"):
// the real SimplArchiveApiClient reads the tenant's label catalog, sets a document's label by id, reads it back
// (name/colour/watermark), sees it on the list row, and rejects an unknown label id.
[Collection(UiCollection.Name)]
public class DesktopSensitivityLabelTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSensitivityLabelTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_and_read_the_label()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // The tenant's seeded default labels are present; pick "Confidential" (watermarked).
        var catalog = await api.GetSensitivityLabelsAsync();
        var confidential = catalog.Items.Single(l => l.Name == "Confidential");
        Assert.True(confidential.Watermark);

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"sens-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("classified"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        Assert.Null((await api.GetDocumentSensitivityAsync(doc.Id)).LabelId); // None by default

        await api.SetSensitivityAsync(doc.Id, confidential.Id);
        var s = await api.GetDocumentSensitivityAsync(doc.Id);
        Assert.Equal(confidential.Id, s.LabelId);
        Assert.Equal("Confidential", s.Name);
        Assert.True(s.Watermark);

        await Assert.ThrowsAsync<ApiActionException>(() => api.SetSensitivityAsync(doc.Id, Guid.NewGuid())); // unknown id → 400

        // The child listing carries the label name/colour so the row can show a badge.
        var listed = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Id == doc.Id);
        Assert.Equal("Confidential", listed.SensitivityLabelName);
    }
}
