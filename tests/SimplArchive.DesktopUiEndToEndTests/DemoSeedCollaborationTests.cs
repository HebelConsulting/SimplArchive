using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimplArchive.UiEndToEndTests;

// The demo seed must contain real collaboration content (issue #380): a chat thread with a REPLY, and version
// check-in comments.
//
// This is a guard, not a feature test. Both are easy to delete by accident and nothing else fails when they go —
// which is exactly what happened before: a live interop export reported `comments=0` and pushed no version
// comments, so two plumbed-and-unit-tested export paths stayed unverified against a real server. Annotations,
// by contrast, were verifiable precisely because the seed had them.
//
// The reply matters specifically: the external-system feed export attaches a reply to its parent post, and a
// flat thread cannot exercise that path at all.
[Collection(UiCollection.Name)]
public class DemoSeedCollaborationTests
{
    private readonly SelfHostedAppFixture _app;

    public DemoSeedCollaborationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_demo_seed_has_a_threaded_chat_and_version_comments()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var offerId = await FindDocumentAsync(http, "Offer 2026-014");
        Assert.NotEqual(Guid.Empty, offerId);

        // --- the chat thread -------------------------------------------------------------------------------
        var messages = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{offerId}/chat"))
            .GetProperty("messages").EnumerateArray().ToList();

        // Typed by a person (kind 0), as opposed to the automatic entries the finalizer records (ADR 0545).
        var userPosts = messages.Where(m => m.GetProperty("kind").GetInt32() == 0).ToList();
        Assert.NotEmpty(userPosts);

        // At least one REPLY — the Feed-threading path the export cannot otherwise exercise.
        var replies = userPosts.Where(m => m.GetProperty("parentMessageId").ValueKind != JsonValueKind.Null).ToList();
        Assert.NotEmpty(replies);

        // Its parent is a top-level post in the same thread, so the thread is genuinely two levels and not a
        // reply pointing at nothing.
        var parentId = replies[0].GetProperty("parentMessageId").GetGuid();
        Assert.Contains(userPosts, m => m.GetProperty("id").GetGuid() == parentId);

        // Two different authors, so the identity card (ADR 0544) is demonstrably per-person in the demo rather
        // than always the logged-in user.
        Assert.True(userPosts.Select(m => m.GetProperty("authorName").GetString()).Distinct().Count() >= 2,
            "the seeded thread should involve more than one person");

        // --- version check-in comments ---------------------------------------------------------------------
        var versions = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{offerId}/versions"))
            .GetProperty("versions").EnumerateArray().ToList();
        Assert.True(versions.Count >= 2, "the offer is the multi-version demo document");

        Assert.All(versions, v => Assert.False(
            string.IsNullOrWhiteSpace(v.GetProperty("comment").GetString()),
            "every version of the offer should carry a check-in comment"));
    }

    // The seeded external link (issue #405). Same guard reasoning as above, with one addition: this link's URL is
    // meant to survive the kiosk's nightly re-seed, so anything that quietly made the token random again would
    // break URLs already shared — while every test that merely creates a link of its own would keep passing.
    [Fact]
    public async Task The_demo_seed_has_a_live_external_link_with_a_stable_url()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var agreementId = await FindDocumentAsync(http, "MyCountry Telekom — service agreement");
        Assert.NotEqual(Guid.Empty, agreementId);

        var link = Assert.Single(
            (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{agreementId}/external-links"))
                .GetProperty("externalLinks").EnumerateArray());

        // Unlimited, not the tenant's default of 5 — five curious visitors must not exhaust the demo.
        Assert.Equal(JsonValueKind.Null, link.GetProperty("maxAccesses").ValueKind);

        // ~90 days out, so a nightly reset always refreshes it long before it lapses.
        var expires = link.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(88), DateTimeOffset.UtcNow.AddDays(91));

        // The URL works ANONYMOUSLY and is the stable one: derived from a fixed seed string, so it is identical
        // after every re-seed. Recomputed here rather than pasted, so the test says WHY the value is what it is.
        var token = Base64Url(SHA256.HashData(
            Encoding.UTF8.GetBytes("simplarchive-demo-telekom-service-agreement-v1")));

        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var redeemed = await anonymous.GetAsync($"/api/external-links/{token}");
        Assert.True(redeemed.IsSuccessStatusCode,
            $"the seeded link must resolve anonymously; got {redeemed.StatusCode}. A 410 here usually means the "
            + "demo tenant's AllowExternalLinks switch was left off — it is checked at access time.");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<Guid> FindDocumentAsync(HttpClient http, string name)
    {
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        foreach (var repo in repos.EnumerateArray())
        {
            if (await FindInAsync(http, repo.GetProperty("id").GetGuid(), name) is { } found)
            {
                return found;
            }
        }

        return Guid.Empty;
    }

    // Depth-first walk of the demo tree — small enough that a plain recursive search is clearer than anything
    // cleverer, and it does not depend on where in the tree the seeder happens to put the document.
    private static async Task<Guid?> FindInAsync(HttpClient http, Guid parentId, string name)
    {
        var children = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{parentId}/children"))
            .GetProperty("children").EnumerateArray().ToList();

        foreach (var child in children)
        {
            if (child.GetProperty("name").GetString() == name)
            {
                return child.GetProperty("id").GetGuid();
            }
        }

        foreach (var child in children.Where(c => c.GetProperty("hasChildren").GetBoolean()))
        {
            if (await FindInAsync(http, child.GetProperty("id").GetGuid(), name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
