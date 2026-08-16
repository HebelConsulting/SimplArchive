using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Group membership (ADR "Group membership editing") through the real DesktopClient api client against the
// running API: list (empty) → add → list (contains, idempotent) → remove → list (empty), on a throwaway
// group + user so it doesn't disturb shared state.
[Collection(UiCollection.Name)]
public class DesktopGroupMembershipTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopGroupMembershipTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Add_list_and_remove_group_members()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var groupId = await client.Admin.CreateGroupAsync("mem-grp-" + suffix);
        var userId = await client.Admin.CreateUserAsync($"mem-{suffix}@example.test", "Mem User " + suffix);

        Assert.Empty(await client.Admin.GetGroupMembersAsync(groupId));

        await client.Admin.AddGroupMemberAsync(groupId, userId.Id);
        var members = await client.Admin.GetGroupMembersAsync(groupId);
        Assert.Contains(members, m => m.Id == userId.Id);

        // The POST is idempotent — adding again doesn't duplicate.
        await client.Admin.AddGroupMemberAsync(groupId, userId.Id);
        Assert.Single(await client.Admin.GetGroupMembersAsync(groupId));

        await client.Admin.RemoveGroupMemberAsync(members.Single(m => m.Id == userId.Id));
        Assert.Empty(await client.Admin.GetGroupMembersAsync(groupId));

        // The group is empty now, so it can be deleted — cleanup.
        await client.Admin.DeleteGroupAsync(groupId);
    }
}
