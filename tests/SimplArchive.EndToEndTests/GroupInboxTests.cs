using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Group inboxes (ADR 0532): a shared, membership-gated staging queue at tenants/{t}/groups/{groupId}/inbox/. Any
// member can upload / list / file; the item appears in every member's inbox labelled with the group; a non-member
// sees none and is refused; a second file of an already-taken item is 404 (idempotent under contention).
[Collection(E2ECollection.Name)]
public class GroupInboxTests
{
    private readonly E2EApiFactory _factory;

    public GroupInboxTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Group_members_share_an_inbox_non_members_are_refused()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var aliceEmail = $"alice-{Guid.NewGuid():N}@e2e.local";
        var bobEmail = $"bob-{Guid.NewGuid():N}@e2e.local";
        var carolEmail = $"carol-{Guid.NewGuid():N}@e2e.local";
        var aliceId = await _factory.SeedUserAsync(tenantId, aliceEmail, "u-1234", "Alice");
        var bobId = await _factory.SeedUserAsync(tenantId, bobEmail, "u-1234", "Bob");
        await _factory.SeedUserAsync(tenantId, carolEmail, "u-1234", "Carol");

        var groupName = $"Scan Team {Guid.NewGuid():N}";
        var groupId = await _factory.SeedGroupWithMemberAsync(tenantId, groupName, aliceId);
        await _factory.AddGroupMemberAsync(tenantId, groupId, bobId); // Alice + Bob are members; Carol is not

        using var alice = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(aliceEmail, "u-1234"));
        using var bob = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bobEmail, "u-1234"));
        using var carol = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(carolEmail, "u-1234"));
        using var storage = new HttpClient();

        // Alice uploads a scan into the GROUP inbox (member-gated).
        var name = $"scan-{Guid.NewGuid():N}.txt";
        var upload = await TestJson.Post(alice, $"/api/inbox?group={groupId}", new { fileName = name });
        (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("scan")))).EnsureSuccessStatusCode();

        // Bob (the other member) sees exactly one item with that name — labelled with the group (Single also proves
        // it isn't duplicated across his own + the group inbox). Group items are opt-in via ?includeGroups=true
        // (the inbox defaults to own-items-only, ADR 0532).
        var groupItem = (await TestJson.Get(bob, "/api/inbox?includeGroups=true")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == name);
        Assert.Equal(groupId, groupItem.GetProperty("groupId").GetGuid());
        Assert.Equal(groupName, groupItem.GetProperty("groupName").GetString());

        // Carol (a non-member) sees none of it, and can't upload into or file from the group inbox.
        Assert.DoesNotContain(
            (await TestJson.Get(carol, "/api/inbox")).GetProperty("items").EnumerateArray().ToList(),
            i => i.GetProperty("name").GetString() == name);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsJsonAsync($"/api/inbox?group={groupId}", new { fileName = "x.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.PostAsJsonAsync($"/api/inbox/{name}/file?group={groupId}", new { folderId = Guid.NewGuid() })).StatusCode);

        // Bob files it into his personal repository → a Document is created and the item LEAVES the group inbox.
        var bobRepo = (await TestJson.Post(bob, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var filed = await TestJson.Post(bob, $"/api/inbox/{name}/file?group={groupId}", new { folderId = bobRepo });
        Assert.False(string.IsNullOrEmpty(filed.GetProperty("name").GetString()));
        Assert.DoesNotContain(
            (await TestJson.Get(bob, "/api/inbox?includeGroups=true")).GetProperty("items").EnumerateArray().ToList(),
            i => i.GetProperty("name").GetString() == name);

        // A second file of the now-taken item → 404 (idempotent under contention — two members draining one queue).
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/inbox/{name}/file?group={groupId}", new { folderId = bobRepo })).StatusCode);
    }
}
