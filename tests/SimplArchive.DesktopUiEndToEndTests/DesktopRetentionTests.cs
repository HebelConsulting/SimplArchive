using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Retention schedule (ADR "Retention policies (auto-disposition)") via the real desktop api client: the demo
// admin (granted CanManageClassification by the demo seed) reads the schedule, which includes the demo document
// (Basic Entry mask, 7-year retention).
[Collection(UiCollection.Name)]
public class DesktopRetentionTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopRetentionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Schedule_includes_the_demo_document()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var schedule = (await api.LegalHolds.GetRetentionScheduleAsync()).Items;

        var demoDoc = schedule.FirstOrDefault(i => i.DocumentName == "Invoice 2026-003");
        Assert.NotNull(demoDoc);
        Assert.Equal(7, demoDoc!.RetentionYears);
        Assert.False(string.IsNullOrEmpty(demoDoc.DispositionDate));
    }
}
