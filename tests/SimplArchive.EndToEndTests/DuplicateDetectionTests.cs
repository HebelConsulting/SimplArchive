using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end for duplicate detection by content hash (ADR "Duplicate document detection") over the real API +
// Postgres + object storage: GET /api/duplicates?hash= finds tenant documents whose latest confirmed version is
// byte-identical, matches on the CURRENT version only, and never reveals a document the caller can't see.
[Collection(E2ECollection.Name)]
public class DuplicateDetectionTests
{
    private readonly E2EApiFactory _factory;

    public DuplicateDetectionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Finds_current_content_duplicates_across_the_tenant()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Dup {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        const string content = "identical duplicate content\n";

        var (aId, hash) = await UploadAsync(api, repoId, "doc-a", content);

        // The hash finds document A (with its path).
        var dupA = (await TestJson.Get(api, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray().ToList();
        Assert.Contains(dupA, d => d.GetProperty("id").GetGuid() == aId);
        Assert.All(dupA, d => Assert.False(string.IsNullOrEmpty(d.GetProperty("path").GetString())));

        // A non-matching hash → no duplicates.
        Assert.Empty((await TestJson.Get(api, $"/api/duplicates?hash={new string('a', 64)}")).GetProperty("duplicates").EnumerateArray());

        // Upload a second document with the SAME content → the hash now finds both.
        var (bId, _) = await UploadAsync(api, repoId, "doc-b", content);
        var both = (await TestJson.Get(api, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(aId, both);
        Assert.Contains(bId, both);

        // Give A a NEW version with different content → A's CURRENT content no longer matches; only B remains.
        await AddVersionAsync(api, aId, "changed content\n");
        var afterChange = (await TestJson.Get(api, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(aId, afterChange);
        Assert.Contains(bId, afterChange);

        // A caller who can't see B is not told about it.
        var (outsiderClientId, outsiderSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        using var outsider = _factory.CreateAuthedClient(await _factory.GetTokenAsync(outsiderClientId, outsiderSecret));
        Assert.Empty((await TestJson.Get(outsider, $"/api/duplicates?hash={hash}")).GetProperty("duplicates").EnumerateArray());
    }

    private static async Task<(Guid DocId, string Hash)> UploadAsync(HttpClient api, Guid repoId, string name, string content)
    {
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        var hash = await AddVersionAsync(api, docId, content);
        return (docId, hash);
    }

    private static async Task<string> AddVersionAsync(HttpClient api, Guid docId, string content)
    {
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
        }

        var finalized = await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return finalized.GetProperty("sha256Hash").GetString()!;
    }

    [Fact]
    public async Task Two_byte_different_emails_sharing_a_Message_ID_meet_by_entryId()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Eml {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // Two copies of "one message", byte-DIFFERENT the way real copies are: each transport hop prepends
        // its own Received: header, so no two recipients' files ever share a hash.
        var messageId = $"<dup-{Guid.NewGuid():N}@corp.example>";
        var (aId, hashA) = await UploadEmlAsync(api, repoId, "copy-a.eml",
            $"Received: from hop-one.example\r\nMessage-ID: {messageId}\r\nSubject: Quarterly\r\nFrom: a@x\r\nTo: b@x\r\n\r\nBody\r\n");
        var (bId, hashB) = await UploadEmlAsync(api, repoId, "copy-b.eml",
            $"Received: from hop-two.example\r\nReceived: from hop-one.example\r\nMessage-ID: {messageId}\r\nSubject: Quarterly\r\nFrom: a@x\r\nTo: b@x\r\n\r\nBody\r\n");
        Assert.NotEqual(hashA, hashB); // the premise: content hashing alone cannot catch these

        // The probe a client makes before filing a THIRD copy: its own hash (matching nothing) plus the
        // Message-ID it extracted client-side. Both existing copies surface. This is also the round trip that
        // pins the client extractor's normalized form against what the finalizer STORED in Entry ID.
        var probe = (await TestJson.Get(api, $"/api/duplicates?hash={new string('b', 64)}&entryId={Uri.EscapeDataString(messageId)}"))
            .GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(aId, probe);
        Assert.Contains(bId, probe);

        // The bracket-less form a MimeKit-style extractor hands over matches too — one normalizer, not two.
        var bare = messageId.Trim('<', '>');
        Assert.Contains(aId, (await TestJson.Get(api, $"/api/duplicates?hash={new string('b', 64)}&entryId={Uri.EscapeDataString(bare)}"))
            .GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));

        // Both keys at once union, deduplicated: hash finds A by content, entryId finds A and B — A once.
        var union = (await TestJson.Get(api, $"/api/duplicates?hash={hashA}&entryId={Uri.EscapeDataString(messageId)}"))
            .GetProperty("duplicates").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(union.Count, union.Distinct().Count());
        Assert.Contains(aId, union);
        Assert.Contains(bId, union);

        // COURTESY, NEVER INVARIANT (#704 rule 1): a caller who cannot see the copies is not told they exist —
        // surfacing "someone you cannot see already filed this" would itself be the ACL breach. Asserting the
        // CANDIDATE is absent from the wire, not merely unrendered, is the calibration the issue asks for.
        var (outsiderClientId, outsiderSecret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: false);
        using var outsider = _factory.CreateAuthedClient(await _factory.GetTokenAsync(outsiderClientId, outsiderSecret));
        Assert.Empty((await TestJson.Get(outsider, $"/api/duplicates?entryId={Uri.EscapeDataString(messageId)}"))
            .GetProperty("duplicates").EnumerateArray());
    }

    [Fact]
    public async Task A_non_email_with_a_matching_field_value_is_not_an_entry_candidate()
    {
        // The identity is the eMail MASK's field, not any field that happens to be called Entry ID — a
        // tenant-authored mask reusing the name must not make its documents "duplicates" of mail. Probed with
        // a REAL collider, because an absent id passes whether or not the mask scoping exists.
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var email = $"collider-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "adm-1234", "Collider", canManageUsers: true, canManageRepositories: true);
        await _factory.GrantTenantAdminAsync(email); // CanManageMasks, to author the colliding mask
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "adm-1234"));

        var messageId = $"<collide-{Guid.NewGuid():N}@x>";
        var mask = await TestJson.Post(admin, "/api/masks", new
        {
            name = $"NotMail {Guid.NewGuid():N}",
            fields = new object[] { new { name = "Entry ID", dataType = 0, isRequired = false } },
        });
        var fieldId = mask.GetProperty("fields").EnumerateArray().Single().GetProperty("id").GetGuid();

        var repoId = (await TestJson.Post(admin, "/api/repositories", new { name = $"NM {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(admin, $"/api/documents/{repoId}/children", new { name = "impostor" })).GetProperty("id").GetGuid();
        await TestJson.Put(admin, $"/api/documents/{docId}/index-data",
            new { fields = new[] { new { fieldDefinitionId = fieldId, values = new[] { messageId } } } });

        var probe = await TestJson.Get(admin, $"/api/duplicates?entryId={Uri.EscapeDataString(messageId)}");
        Assert.Empty(probe.GetProperty("duplicates").EnumerateArray());
    }

    /// <summary>Uploads an .eml whose Entry ID the FINALIZER extracts — the server half of the round trip.</summary>
    private static async Task<(Guid DocId, string Hash)> UploadEmlAsync(HttpClient api, Guid repoId, string name, string rfc822)
    {
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".eml" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(rfc822)))).EnsureSuccessStatusCode();
        }

        var finalized = await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return (docId, finalized.GetProperty("sha256Hash").GetString()!);
    }
}
