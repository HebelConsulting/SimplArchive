using System.Net.Http.Json;
using MailKit.Net.Imap;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// A reference is another appearance of a folder (ADR "Desktop drag-and-drop move and reference"), and the
// clients already show them beside the children. ADR 0627 names the gap this closes: the IMAP catalog walked
// child documents only, so a filing destination a user referenced into their personal space was invisible to
// their mail client — which is exactly what Goal 1(b) needs to work.
[Collection(E2ECollection.Name)]
public class ImapReferenceMailboxTests
{
    private readonly E2EApiFactory _factory;

    public ImapReferenceMailboxTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, string Email, string ImapPassword, Guid TenantId, HttpClient Owner)> UserAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"imapref-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Imap Ref User");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var generated = await TestJson.Post(api, "/api/me/imap-access", new { });
        return (api, email, generated.GetProperty("password").GetString()!, tenantId, owner);
    }

    private static async Task<string[]> MailboxesAsync(int port, string email, string imapPassword)
    {
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);
        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        return folders.Select(f => f.FullName).ToArray();
    }

    private int Port => ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

    [Fact]
    public async Task A_referenced_folder_appears_as_a_mailbox_with_its_whole_subtree()
    {
        var (api, email, imapPassword, _, owner) = await UserAsync();
        using var _a = api;
        using var _o = owner;

        // A repository with a nested folder — the subtree is the point: filing a mail means dropping it into
        // Sales/2026/Invoices, not into Sales.
        var repoName = $"Ref{Guid.NewGuid():N}"[..10];
        var repo = await TestJson.Post(owner, "/api/repositories", new { name = repoName });
        var repoId = repo.GetProperty("id").GetGuid();
        var year = await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "2026" });
        var yearId = year.GetProperty("id").GetGuid();
        await TestJson.Post(api, $"/api/documents/{yearId}/children", new { name = "Invoices" });

        // The user's personal space, and a reference filed into it pointing at the year folder.
        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        await TestJson.Post(api, $"/api/documents/{personalId}/references", new { targetId = yearId });

        var mailboxes = await MailboxesAsync(Port, email, imapPassword);

        // INBOX is the personal repository root, so the reference hangs beneath it — and its child came along.
        Assert.Contains("INBOX/2026", mailboxes);
        Assert.Contains("INBOX/2026/Invoices", mailboxes);
    }

    [Fact]
    public async Task A_reference_that_became_a_cycle_by_a_later_move_does_not_recurse_for_ever()
    {
        // The API refuses to file a reference into its own target's ancestry up front (INVALID_REFERENCE_TARGET),
        // so the obvious cycle cannot be created directly — and writing the test the obvious way is how that got
        // discovered rather than assumed.
        //
        // But that check runs at the controller when the reference is MADE, and nothing re-runs it when the
        // tree moves underneath. File a reference between two siblings, then move the holder under the target,
        // and the cycle exists in data the API considers legal: B contains A, and A references B.
        //
        // Without the ancestor-chain guard the walk yields INBOX/B/A/B/A/… until the stack or the client gives
        // up, and it takes the WHOLE catalog with it rather than just this branch — so the assertion is that
        // LIST completes at all.
        var (api, email, imapPassword, _, owner) = await UserAsync();
        using var _a = api;
        using var _o = owner;

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var holder = await TestJson.Post(api, $"/api/documents/{personalId}/children", new { name = "Holder" });
        var holderId = holder.GetProperty("id").GetGuid();
        var target = await TestJson.Post(api, $"/api/documents/{personalId}/children", new { name = "Target" });
        var targetId = target.GetProperty("id").GetGuid();

        // Legal today: they are siblings, so neither is the other's ancestor.
        await TestJson.Post(api, $"/api/documents/{holderId}/references", new { targetId });

        // …and now the move that makes it a cycle. If this is ever refused too, this test starts failing HERE,
        // which is the right place to find out that the guard below has become unreachable.
        var etag = (await api.GetAsync($"/api/documents/{holderId}")).Headers.ETag!.Tag;
        using var move = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{holderId}/parent")
        {
            Content = JsonContent.Create(new { parentId = targetId }),
        };
        move.Headers.TryAddWithoutValidation("If-Match", etag);
        (await api.SendAsync(move)).EnsureSuccessStatusCode();

        var mailboxes = await MailboxesAsync(Port, email, imapPassword);

        Assert.Contains("INBOX/Target", mailboxes);
        Assert.Contains("INBOX/Target/Holder", mailboxes);

        // The looping appearance is omitted rather than followed.
        Assert.DoesNotContain(mailboxes, m => m.StartsWith("INBOX/Target/Holder/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reference_to_a_folder_the_user_cannot_see_is_not_projected()
    {
        // A reference must not become a way around the ACL, and the test has to be built so it could FAIL: the
        // reference is filed into a repository the second user CAN see, pointing at one they cannot. Putting it
        // in the first user's personal space instead would pass no matter what the code did, because the second
        // user never walks that space at all.
        var (api, _, _, tenantId, owner) = await UserAsync();
        using var _a = api;
        using var _o = owner;

        var sharedName = $"Shared{Guid.NewGuid():N}"[..12];
        var shared = await TestJson.Post(owner, "/api/repositories", new { name = sharedName });
        var sharedId = shared.GetProperty("id").GetGuid();

        var secretName = $"Secret{Guid.NewGuid():N}"[..12];
        var secret = await TestJson.Post(owner, "/api/repositories", new { name = secretName });
        var secretId = secret.GetProperty("id").GetGuid();

        await TestJson.Post(api, $"/api/documents/{sharedId}/references", new { targetId = secretId });

        var otherEmail = $"imapref-other-{Guid.NewGuid():N}@e2e.local";
        const string otherPassword = "imap-1234";
        var otherId = await _factory.SeedUserAsync(tenantId, otherEmail, otherPassword, "Other User");
        (await owner.PutAsJsonAsync($"/api/documents/{sharedId}/acl-entries/users/{otherId}",
            new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();

        using var otherApi = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(otherEmail, otherPassword));
        var otherImap = (await TestJson.Post(otherApi, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;

        var mailboxes = await MailboxesAsync(Port, otherEmail, otherImap);

        // They reach the repository holding the reference…
        Assert.Contains(sharedName, mailboxes);

        // …but the reference itself resolves to nothing they may see.
        Assert.DoesNotContain(mailboxes, m => m.Contains(secretName, StringComparison.Ordinal));
    }
}
