using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Per-user personal repository (ADR "Per-user personal repository") through the real DesktopClient api client
// against the running API: get-or-create is idempotent + excluded from the shared repository list, and two users'
// private spaces are isolated — neither can list the other's personal repository contents.
[Collection(UiCollection.Name)]
public class DesktopPersonalRepositoryTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopPersonalRepositoryTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Personal_repositories_are_get_or_create_excluded_from_the_list_and_isolated_per_user()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Two throwaway users with known passwords, each logging in for their own token.
        var aliceId = await admin.CreateUserAsync($"alice-{suffix}@example.test", "Alice " + suffix);
        var bobId = await admin.CreateUserAsync($"bob-{suffix}@example.test", "Bob " + suffix);
        var alicePw = await admin.ResetUserPasswordAsync(aliceId);
        var bobPw = await admin.ResetUserPasswordAsync(bobId);

        var alice = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, $"alice-{suffix}@example.test", alicePw));
        var bob = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, $"bob-{suffix}@example.test", bobPw));

        // Get-or-create is idempotent and gives each user a distinct space.
        var aliceRepo = await alice.GetPersonalRepositoryAsync();
        Assert.NotNull(aliceRepo);
        Assert.Equal("Personal", aliceRepo!.Name);
        Assert.Equal(aliceRepo.Id, (await alice.GetPersonalRepositoryAsync())!.Id);

        var bobRepo = await bob.GetPersonalRepositoryAsync();
        Assert.NotNull(bobRepo);
        Assert.NotEqual(aliceRepo.Id, bobRepo!.Id);

        // Alice files a private folder into her personal repository.
        await alice.CreateFolderAsync(aliceRepo.Id, "alice-private-" + suffix);
        Assert.Contains(await alice.GetChildrenAsync(aliceRepo.Id), c => c.Name == "alice-private-" + suffix);

        // Bob can't list Alice's personal repository (no ACL grant → the API denies it).
        await Assert.ThrowsAsync<HttpRequestException>(() => bob.GetChildrenAsync(aliceRepo.Id));

        // Neither personal repository appears in the other user's shared repository list.
        Assert.DoesNotContain(await bob.GetRepositoriesAsync(), r => r.Id == aliceRepo.Id);
        Assert.DoesNotContain(await alice.GetRepositoriesAsync(), r => r.Id == bobRepo.Id);
    }
}
