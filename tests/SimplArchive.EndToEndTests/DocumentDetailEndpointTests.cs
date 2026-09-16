using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The combined detail save (ADRs 0794/0796): one request, one If-Match, one transaction, per-aspect audit.
//
// Driven end to end because every claim this endpoint makes is about what happens ACROSS aspects — that a
// refusal leaves nothing written, that the gate depends on which aspects changed, that a rename still records
// Document.Renamed. None of that is visible to a test of any single aspect, which is exactly how the eight
// separate writes looked correct for so long.
//
// Each test creates its own document: these MUTATE, and shared seeded data is for reading.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentDetailEndpointTests
{
    private readonly E2EApiFactory _factory;

    public DocumentDetailEndpointTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task One_request_saves_every_aspect_and_records_one_event_per_aspect()
    {
        var (owner, tenantId, docId, _) = await DocumentAsync();
        using var _o = owner;
        var maskId = await _factory.SeedMaskWithRetentionAsync(tenantId, retentionYears: 5);

        var detail = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        var etag = detail.GetProperty("etag").GetString();

        var saved = await PutDetailAsync(owner, docId, etag!, new
        {
            name = "detail-renamed",
            documentDate = "2021-03-04",
            tags = new[] { "alpha", "beta" },
            maskId,
            fields = Array.Empty<object>(),
            ocrLanguages = Array.Empty<string>(),
        });

        Assert.Equal("detail-renamed", saved.GetProperty("name").GetString());
        Assert.Equal("2021-03-04", saved.GetProperty("documentDate").GetString());
        Assert.Equal(maskId, saved.GetProperty("maskId").GetGuid());
        Assert.Equal(["alpha", "beta"], saved.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());

        // Per-aspect actions, NOT one "detail updated": a rename must be findable as Document.Renamed whichever
        // door it came through, or the audit log answers differently depending on which client was used.
        var actions = await AuditActionsAsync(tenantId);
        Assert.Contains("Document.Renamed", actions);
        Assert.Contains("Document.DocumentDateChanged", actions);
        Assert.Contains("Document.TagsUpdated", actions);
        Assert.Contains("Document.MaskAssigned", actions);
    }

    [Fact]
    public async Task A_refused_save_writes_none_of_the_aspects_that_would_have_succeeded()
    {
        var (owner, _, docId, _) = await DocumentAsync();
        using var _o = owner;

        var before = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        var etag = before.GetProperty("etag").GetString();

        // A rename and a tag that would both succeed, alongside a mask id that does not exist. Under the eight
        // separate writes the rename had already committed by the time the mask failed.
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/detail")
        {
            Content = JsonContent.Create(new
            {
                name = "should-not-persist",
                tags = new[] { "should-not-persist" },
                maskId = Guid.NewGuid(),
                fields = Array.Empty<object>(),
                ocrLanguages = Array.Empty<string>(),
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.False((await owner.SendAsync(request)).IsSuccessStatusCode);

        var after = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        Assert.Equal(before.GetProperty("name").GetString(), after.GetProperty("name").GetString());
        Assert.Empty(after.GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task The_precondition_is_the_one_the_form_was_loaded_with()
    {
        var (owner, _, docId, _) = await DocumentAsync();
        using var _o = owner;

        var detail = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        var formEtag = detail.GetProperty("etag").GetString()!;

        // CARRIES THE MASK, because this endpoint states the full intended value — the API serves no PATCH.
        // Omitting it used to mean "clear the mask", and this request would silently have UNTYPED the document
        // while the test looked only at names; it passed because the document had no mask to lose. Since #1240
        // there is no untyped state, so the omission is refused outright (400 DOCUMENT_MUST_WEAR_A_MASK) — and
        // a test about PRECONDITIONS should not be quietly asserting anything about masks either way.
        var maskId = detail.GetProperty("maskId").GetGuid();

        object body(string name) => new
        {
            name,
            maskId,
            tags = Array.Empty<string>(),
            fields = Array.Empty<object>(),
            ocrLanguages = Array.Empty<string>(),
        };

        // No precondition at all is refused outright — this endpoint REQUIRES it, unlike the sub-resources.
        using var none = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/detail")
        {
            Content = JsonContent.Create(body("no-if-match")),
        };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await owner.SendAsync(none)).StatusCode);

        // Somebody else writes while the form is open.
        await PutDetailAsync(owner, docId, formEtag, body("somebody-else"));

        // The form's own tag is now stale, and that is precisely the case the mechanism exists to catch — the
        // re-read-immediately-before-writing pattern it replaces could never see it.
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/detail")
        {
            Content = JsonContent.Create(body("from-a-stale-form")),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", formEtag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await owner.SendAsync(stale)).StatusCode);

        var after = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        Assert.Equal("somebody-else", after.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_save_that_changes_nothing_leaves_the_tag_alone()
    {
        var (owner, _, docId, _) = await DocumentAsync();
        using var _o = owner;

        var before = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        var etag = before.GetProperty("etag").GetString()!;

        // Echoing back exactly what was read is not a change, so it must not mint a new version — otherwise
        // every open form elsewhere would be invalidated by somebody pressing Save on an unedited pane.
        //
        // It echoes the WHOLE detail, which is also the contract: a PUT states the full intended value, so a
        // request that omits maskId is asking to CLEAR the mask, not to leave it alone. (This test asserted
        // the opposite at first and was right to fail — the document carries the mask auto-classification gave
        // it at finalize.)
        var after = await PutDetailAsync(owner, docId, etag, Echo(before));

        Assert.Equal(etag, after.GetProperty("etag").GetString());
    }

    [Fact]
    public async Task Tags_alone_stay_writable_under_a_legal_hold_while_anything_else_is_refused()
    {
        var (owner, tenantId, docId, _) = await DocumentAsync();
        using var _o = owner;

        var detail = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        var etag = detail.GetProperty("etag").GetString()!;
        var name = detail.GetProperty("name").GetString();

        // A ServiceAccount cannot place a hold, so a CanLegalHold user does it — the document stays the
        // service account's, whose full rights put its attempts past the rights check and onto the hold guard.
        var email = $"detail-legal-{Guid.NewGuid():N}@e2e.local";
        const string password = "detail-legal-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Compliance");
        await _factory.GrantCanLegalHoldAsync(email);
        using var compliance = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var holdId = (await TestJson.Post(compliance, "/api/legal-holds", new { name = $"Detail {Guid.NewGuid():N}", reason = "detail gate" })).GetProperty("id").GetGuid();
        (await compliance.PostAsJsonAsync($"/api/legal-holds/{holdId}/items", new { documentId = docId })).EnsureSuccessStatusCode();

        // ADR 0796: the gate is the union of the gates of the aspects that CHANGE. Tags carry no legal-hold
        // block — deliberately, like comments — and they are edited inside the same pencil, so a blanket strict
        // gate here would have removed a capability nobody decided to remove.
        var tagged = await PutDetailAsync(owner, docId, etag, Echo(detail, tags: ["still-taggable"]));
        Assert.Equal(["still-taggable"], tagged.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());

        using var renaming = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/detail")
        {
            Content = JsonContent.Create(Echo(detail, tags: ["still-taggable"], name: "frozen-rename")),
        };
        renaming.Headers.TryAddWithoutValidation("If-Match", tagged.GetProperty("etag").GetString());
        Assert.False((await owner.SendAsync(renaming)).IsSuccessStatusCode);

        var after = await TestJson.Get(owner, $"/api/documents/{docId}/detail");
        Assert.Equal(name, after.GetProperty("name").GetString());
    }

    // The whole detail back, the way a client that loaded the form sends it. Echoing matters: this is a PUT,
    // so an omitted aspect is a request to CLEAR it, not to leave it alone.
    private static object Echo(System.Text.Json.JsonElement detail, string[]? tags = null, string? name = null) => new
    {
        name = name ?? detail.GetProperty("name").GetString(),
        documentDate = detail.GetProperty("documentDate").GetString(),
        documentTime = detail.GetProperty("documentTime").GetString(),
        ocrLanguages = detail.GetProperty("ocrLanguages").EnumerateArray().Select(l => l.GetString()!).ToArray(),
        sensitivityLabelId = GuidOrNull(detail, "sensitivityLabelId"),
        tags = tags ?? detail.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray(),
        fields = detail.GetProperty("fields").EnumerateArray()
            .Select(f => new
            {
                fieldDefinitionId = f.GetProperty("fieldDefinitionId").GetGuid(),
                values = f.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            })
            .ToArray(),
        maskId = GuidOrNull(detail, "maskId"),
        contentsSortOrder = detail.GetProperty("contentsSortOrder").ValueKind == System.Text.Json.JsonValueKind.Null
            ? (int?)null
            : detail.GetProperty("contentsSortOrder").GetInt32(),
    };

    private static Guid? GuidOrNull(System.Text.Json.JsonElement detail, string property) =>
        detail.GetProperty(property).ValueKind == System.Text.Json.JsonValueKind.Null
            ? null
            : detail.GetProperty(property).GetGuid();

    private static async Task<System.Text.Json.JsonElement> PutDetailAsync(
        HttpClient api, Guid documentId, string etag, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/detail")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        var response = await api.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    private async Task<HashSet<string?>> AuditActionsAsync(Guid tenantId)
    {
        var viewerEmail = $"detail-auditor-{Guid.NewGuid():N}@e2e.local";
        const string password = "detail-1234";
        await _factory.SeedUserAsync(tenantId, viewerEmail, password, "Detail auditor", canViewAuditLog: true);
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, password));

        return (await TestJson.Get(viewer, "/api/audit-events?limit=200")).GetProperty("events")
            .EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToHashSet();
    }

    // Its own repository, document and confirmed version per test — these all mutate what they touch.
    private async Task<(HttpClient Owner, Guid TenantId, Guid DocumentId, Guid VersionId)> DocumentAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Detail {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"detail-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("detail")))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        return (owner, tenantId, docId, versionId);
    }
}
