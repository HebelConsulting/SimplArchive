using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Intray item send/move + admin visibility (ADR 0532): a user can move an own item into a group or another user's
// intray; a member can claim a group item into their own; a CanManageIntrayes holder can open + drain any user's
// intray via ?user=, while a non-admin cannot.
[Collection(E2ECollection.Name)]
public class IntrayMoveTests
{
    private readonly E2EApiFactory _factory;

    public IntrayMoveTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Send_to_group_and_user_claim_and_admin_triage()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        var bobEmail = $"bob-{Guid.NewGuid():N}@e2e.local";
        var carolEmail = $"carol-{Guid.NewGuid():N}@e2e.local";
        var adminId = await _factory.SeedUserAsync(tenantId, adminEmail, "u-1234", "Admin", canManageIntrayes: true);
        var bobId = await _factory.SeedUserAsync(tenantId, bobEmail, "u-1234", "Bob");
        var carolId = await _factory.SeedUserAsync(tenantId, carolEmail, "u-1234", "Carol");

        // A group Bob + Admin both belong to.
        var groupId = await _factory.SeedGroupWithMemberAsync(tenantId, $"Team {Guid.NewGuid():N}", bobId);
        await _factory.AddGroupMemberAsync(tenantId, groupId, adminId);

        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "u-1234"));
        using var bob = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bobEmail, "u-1234"));
        using var carol = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(carolEmail, "u-1234"));
        using var storage = new HttpClient();

        async Task UploadToOwnAsync(HttpClient c, string n)
        {
            var up = await TestJson.Post(c, "/api/intray", new { fileName = n });
            (await storage.PutAsync(up.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("x")))).EnsureSuccessStatusCode();
        }
        static async Task<List<string>> NamesAsync(HttpClient c, string url) =>
            (await TestJson.Get(c, url)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();

        // 1) Bob sends an own item into the GROUP intray → it leaves his own, and Admin (a member) sees it there.
        var toGroup = $"g-{Guid.NewGuid():N}.txt";
        await UploadToOwnAsync(bob, toGroup);
        (await bob.PostAsJsonAsync($"/api/intray/{toGroup}/move", new { targetGroupId = groupId })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(toGroup, await NamesAsync(bob, "/api/intray"));                       // gone from Bob's own
        Assert.Contains(toGroup, await NamesAsync(admin, "/api/intray?includeGroups=true"));        // now in the group

        // 2) Bob sends an own item into CAROL's intray (a hand-off) → Carol sees it as her own.
        var toCarol = $"u-{Guid.NewGuid():N}.txt";
        await UploadToOwnAsync(bob, toCarol);
        (await bob.PostAsJsonAsync($"/api/intray/{toCarol}/move", new { targetUserId = carolId })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(toCarol, await NamesAsync(bob, "/api/intray"));
        Assert.Contains(toCarol, await NamesAsync(carol, "/api/intray"));

        // 3) A member claims the group item into their OWN intray (source = ?group=, target = self).
        (await admin.PostAsJsonAsync($"/api/intray/{toGroup}/move?group={groupId}", new { targetUserId = adminId })).EnsureSuccessStatusCode();
        Assert.Contains(toGroup, await NamesAsync(admin, "/api/intray"));                           // now in Admin's own
        Assert.DoesNotContain(toGroup, await NamesAsync(bob, "/api/intray?includeGroups=true"));    // left the group (Bob, a member, no longer sees it)

        // 4) Admin (CanManageIntrayes) opens Carol's intray via ?user= and claims the handed-off item.
        Assert.Contains(toCarol, await NamesAsync(admin, $"/api/intray?user={carolId}"));
        (await admin.PostAsJsonAsync($"/api/intray/{toCarol}/move?user={carolId}", new { targetUserId = adminId })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(toCarol, await NamesAsync(carol, "/api/intray"));                     // left Carol's
        Assert.Contains(toCarol, await NamesAsync(admin, "/api/intray"));                           // now Admin's

        // 5) A non-admin cannot open another user's intray.
        Assert.Equal(HttpStatusCode.Forbidden, (await carol.GetAsync($"/api/intray?user={bobId}")).StatusCode);
    }
}
