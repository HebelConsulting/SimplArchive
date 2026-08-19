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
        var aliceId = await admin.Admin.CreateUserAsync($"alice-{suffix}@example.test", "Alice " + suffix);
        var bobId = await admin.Admin.CreateUserAsync($"bob-{suffix}@example.test", "Bob " + suffix);
        var alicePw = await admin.Admin.ResetUserPasswordAsync(aliceId);
        var bobPw = await admin.Admin.ResetUserPasswordAsync(bobId);

        var alice = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, $"alice-{suffix}@example.test", alicePw));
        var bob = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, $"bob-{suffix}@example.test", bobPw));

        // Get-or-create is idempotent and gives each user a distinct space.
        var aliceRepo = await alice.Profile.GetPersonalRepositoryAsync();
        Assert.NotNull(aliceRepo);
        Assert.Equal("Personal", aliceRepo!.Name);
        Assert.Equal(aliceRepo.Id, (await alice.Profile.GetPersonalRepositoryAsync())!.Id);

        var bobRepo = await bob.Profile.GetPersonalRepositoryAsync();
        Assert.NotNull(bobRepo);
        Assert.NotEqual(aliceRepo.Id, bobRepo!.Id);

        // Alice files a private folder into her personal repository — inside My Documents, because the space's
        // first level holds only the folders it was provisioned with (#634). What this test is about is that
        // BOB cannot see it, and that is unchanged by which level it sits on.
        var aliceDocs = (await alice.Documents.GetChildrenAsync(aliceRepo.Href("children")))
            .Single(c => c.Name == "My Documents");
        await alice.Documents.CreateFolderAsync(aliceDocs.Href("children"), "alice-private-" + suffix);
        Assert.Contains(await alice.Documents.GetChildrenAsync(aliceDocs.Href("children")), c => c.Name == "alice-private-" + suffix);

        // Bob can't list Alice's personal repository (no ACL grant → the API denies it).
        await Assert.ThrowsAsync<HttpRequestException>(() => bob.Documents.GetChildrenAsync(aliceRepo.Href("children")));

        // Neither personal repository appears in the other user's shared repository list.
        Assert.DoesNotContain(await bob.Documents.GetRepositoriesAsync(), r => r.Id == aliceRepo.Id);
        Assert.DoesNotContain(await alice.Documents.GetRepositoriesAsync(), r => r.Id == bobRepo.Id);
    }
}
