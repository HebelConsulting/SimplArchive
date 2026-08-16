using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of saved searches (ADR "Saved searches"): the real SimplArchiveApiClient saves, lists,
// rejects a duplicate name, and deletes a saved search.
[Collection(UiCollection.Name)]
public class DesktopSavedSearchesTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSavedSearchesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Save_list_conflict_and_delete()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"dt-saved-{suffix}";
        var query = $"q=inv{suffix}&system[documentType][eq]=Invoice";

        await api.Search.SaveSearchAsync(name, query);

        var saved = (await api.Search.GetSavedSearchesAsync()).Single(s => s.Name == name);
        Assert.Equal(query, saved.QueryString);

        // A duplicate name is rejected.
        await Assert.ThrowsAsync<ApiActionException>(() => api.Search.SaveSearchAsync(name, "q=other"));

        // Delete → gone.
        await api.Search.DeleteSavedSearchAsync(saved);
        Assert.DoesNotContain(await api.Search.GetSavedSearchesAsync(), s => s.Id == saved.Id);
    }

    [Fact]
    public async Task Share_scope_everyone_specific_and_private()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var name = $"dt-share-{Guid.NewGuid().ToString("N")[..8]}";

        await api.Search.SaveSearchAsync(name, "q=shared");
        var saved = (await api.Search.GetSavedSearchesAsync()).Single(s => s.Name == name);
        Assert.True(saved.IsMine);
        Assert.Equal(0, saved.ShareScope); // Private by default

        // Share with everyone (scope 1) (ADR "Scoped saved-search sharing").
        await api.Search.SetSavedSearchShareAsync(saved, 1, []);
        Assert.Equal(1, (await api.Search.GetSavedSearchesAsync()).Single(s => s.Id == saved.Id).ShareScope);

        // Narrow to specific: the share-targets picker lists real principals; share with the first user.
        var targets = await api.Search.GetShareTargetsAsync();
        var aUser = targets.First(t => t.Type == "user");
        await api.Search.SetSavedSearchShareAsync(saved, 2, [(aUser.Type, aUser.Id)]);
        Assert.Equal(2, (await api.Search.GetSavedSearchesAsync()).Single(s => s.Id == saved.Id).ShareScope);
        var grants = await api.Search.GetSavedSearchSharesAsync(saved);
        Assert.Equal(aUser.Id, Assert.Single(grants).PrincipalId);

        // Back to private (scope 0) — the specific grants are cleared.
        await api.Search.SetSavedSearchShareAsync(saved, 0, []);
        Assert.Equal(0, (await api.Search.GetSavedSearchesAsync()).Single(s => s.Id == saved.Id).ShareScope);
        Assert.Empty(await api.Search.GetSavedSearchSharesAsync(saved));

        await api.Search.DeleteSavedSearchAsync(saved);
    }
}
