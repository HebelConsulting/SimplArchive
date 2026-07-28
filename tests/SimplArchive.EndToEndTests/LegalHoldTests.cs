using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising legal hold (ADR "Legal hold & retention enforcement"):
// a CanLegalHold user places a hold on a document; while held, new versions / mask changes / deletion are all
// refused (409 LEGAL_HOLD); releasing the hold unfreezes it. A non-CanLegalHold caller can't place holds.
[Collection(E2ECollection.Name)]
public class LegalHoldTests
{
    private readonly E2EApiFactory _factory;

    public LegalHoldTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_held_document_is_frozen_until_the_hold_is_released()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A user with CanLegalHold to place the hold (a ServiceAccount can't).
        var email = $"legal-{Guid.NewGuid():N}@e2e.local";
        const string password = "legal-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Compliance", canResetMfa: false);
        await _factory.GrantCanLegalHoldAsync(email);
        using var compliance = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // The ServiceAccount owns a repo + document (full rights via the auto-grant), so its frozen-mutation
        // attempts get past the rights check and hit the legal-hold guard (409, not 403).
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Hold {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "held-doc" })).GetProperty("id").GetGuid();

        // Place a legal hold covering the document.
        var holdId = (await TestJson.Post(compliance, "/api/legal-holds", new { name = "Matter 42", reason = "litigation" })).GetProperty("id").GetGuid();
        (await compliance.PostAsJsonAsync($"/api/legal-holds/{holdId}/items", new { documentId = docId })).EnsureSuccessStatusCode();

        // The document now reports it's on hold.
        Assert.True((await TestJson.Get(owner, $"/api/documents/{docId}")).GetProperty("onLegalHold").GetBoolean());

        // Frozen: a new version, a mask change, and deletion are all refused with 409 LEGAL_HOLD.
        var newVersion = await owner.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        Assert.Equal(HttpStatusCode.Conflict, newVersion.StatusCode);

        var maskChange = await owner.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId = SimplArchive.Domain.Masks.WellKnownMaskIds.BasicEntry });
        Assert.Equal(HttpStatusCode.Conflict, maskChange.StatusCode);

        var deleteWhileHeld = await SendDeleteAsync(owner, docId);
        Assert.Equal(HttpStatusCode.Conflict, deleteWhileHeld.StatusCode);

        // Release the hold → the document unfreezes and can be deleted.
        (await compliance.PostAsync($"/api/legal-holds/{holdId}/release", null)).EnsureSuccessStatusCode();
        Assert.False((await TestJson.Get(owner, $"/api/documents/{docId}")).GetProperty("onLegalHold").GetBoolean());
        var deleteAfterRelease = await SendDeleteAsync(owner, docId);
        Assert.True(deleteAfterRelease.IsSuccessStatusCode, $"expected success, got {deleteAfterRelease.StatusCode}");
    }

    [Fact]
    public async Task A_caller_without_CanLegalHold_cannot_place_holds()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"plain-{Guid.NewGuid():N}@e2e.local";
        const string password = "plain-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Plain");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/legal-holds", new { name = "Nope" })).StatusCode);
    }

    // Delete needs an If-Match; fetch the current ETag via HEAD, then send it.
    private static async Task<HttpResponseMessage> SendDeleteAsync(HttpClient client, Guid documentId)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{documentId}");
        var etag = (await client.SendAsync(head)).Headers.ETag!.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{documentId}");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
}
