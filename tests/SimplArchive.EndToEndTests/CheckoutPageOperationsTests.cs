using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.EndToEndTests;

// Rotate/Sort on a check-out's WORKING COPY (#549, ADR 0593), driven the way a conforming client must: the
// checkouts row advertises `pages`, the pages resource advertises `sort`, and every action follows those rels
// (ADR 0543). The subject is the stash — created lazily from the archived version on the first operation — and
// the archive is proven UNTOUCHED until a normal check-in promotes the result.
//
// Pages get DIFFERENT WIDTHS (the IntrayPageOperationsTests identity trick): reading widths back out of the
// stash says which page actually went where, and PdfPig reports rotation through width/height swapping.
[Collection(E2ECollection.Name)]
public class CheckoutPageOperationsTests
{
    private readonly E2EApiFactory _factory;

    public CheckoutPageOperationsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Sorting_rewrites_the_stash_only_and_checkin_promotes_it()
    {
        var (owner, holder, docId, _) = await CheckedOutPdfAsync(300, 400, 500);

        // The row advertises `pages`; the resource says 3 pages and offers `sort`.
        var row = await CheckoutRowAsync(holder, docId);
        var pages = await TestJson.Get(holder, Rel(row, "pages"));
        Assert.Equal(3, pages.GetProperty("pageCount").GetInt32());

        // Keep pages 3 and 1 (dropping 2), rotate the kept page 1 a quarter turn — one request, into the stash.
        var response = await holder.PostAsJsonAsync(Rel(pages, "sort"), new
        {
            pageOrder = new[] { 3, 1 },
            rotations = new[] { new { page = 1, degrees = 90 } },
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The stash now exists and holds the arrangement: page 3 (width 500), then page 1 rotated (800 —
        // PdfPig reads the /Rotate through as a width/height swap). The ARCHIVE still has all three pages.
        row = await CheckoutRowAsync(holder, docId);
        Assert.True(row.GetProperty("hasStash").GetBoolean());
        Assert.Equal([500, 800], await WidthsAsync(row.GetProperty("stashDownloadUrl").GetString()!));
        Assert.Equal([300, 400, 500], await WidthsAsync(row.GetProperty("downloadUrl").GetString()!));

        // A second operation reads the STASH, not the archive: reversing the two survivors proves the source.
        pages = await TestJson.Get(holder, Rel(row, "pages"));
        Assert.Equal(2, pages.GetProperty("pageCount").GetInt32());
        (await holder.PostAsJsonAsync(Rel(pages, "sort"), new { pageOrder = new[] { 2, 1 } })).EnsureSuccessStatusCode();
        row = await CheckoutRowAsync(holder, docId);
        Assert.Equal([800, 500], await WidthsAsync(row.GetProperty("stashDownloadUrl").GetString()!));

        // An invalid order (a duplicate) is refused whole and changes nothing.
        var refused = await holder.PostAsJsonAsync(Rel(pages, "sort"), new { pageOrder = new[] { 1, 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("CHECKOUT_PAGE_ORDER_INVALID",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        row = await CheckoutRowAsync(holder, docId);
        Assert.Equal([800, 500], await WidthsAsync(row.GetProperty("stashDownloadUrl").GetString()!));

        // Check-in promotes the reshaped working copy to the new current version — the ONLY way the archive changes.
        (await holder.PostAsync(Rel(row, "checkin"), null)).EnsureSuccessStatusCode();
        var document = await TestJson.Get(owner, $"/api/documents/{docId}");
        var versions = await TestJson.Get(owner, Rel(document, "versions"));
        var currentId = versions.GetProperty("currentVersionId").GetGuid();
        var current = versions.GetProperty("versions").EnumerateArray()
            .Single(v => v.GetProperty("id").GetGuid() == currentId);
        Assert.Equal([800, 500], await WidthsAsync(Rel(current, "download")));
    }

    [Fact]
    public async Task Only_the_lock_holder_reaches_the_working_copys_pages()
    {
        var (_, holder, docId, tenantId) = await CheckedOutPdfAsync(300, 400);
        var row = await CheckoutRowAsync(holder, docId);
        var pagesHref = Rel(row, "pages");
        var sortHref = Rel(await TestJson.Get(holder, pagesHref), "sort");

        // Another user — even a tenant admin — is not the holder: both actions are refused, nothing is written.
        var (_, other) = await SeedAdminAsync(tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(pagesHref)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(sortHref, new { pageOrder = new[] { 2, 1 } })).StatusCode);
        Assert.False((await CheckoutRowAsync(holder, docId)).GetProperty("hasStash").GetBoolean());
    }

    // A repo + document owned by the seeding ServiceAccount, its confirmed current version the given PDF,
    // checked out by a fresh interactive admin.
    private async Task<(HttpClient Owner, HttpClient Holder, Guid DocId, Guid TenantId)> CheckedOutPdfAsync(params int[] pageWidths)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"COP {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "paged-doc" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".pdf" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Pdf(pageWidths)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });

        var (_, holder) = await SeedAdminAsync(tenantId);
        (await holder.PutAsync($"/api/documents/{docId}/checkout", null)).EnsureSuccessStatusCode();
        return (owner, holder, docId, tenantId);
    }

    private async Task<(string Email, HttpClient Client)> SeedAdminAsync(Guid tenantId)
    {
        var email = $"cop-{Guid.NewGuid():N}@e2e.local";
        const string password = "cop-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Editor");
        await _factory.GrantTenantAdminAsync(email); // ACL bypass → CanEditContent on any document
        return (email, _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password)));
    }

    private static async Task<JsonElement> CheckoutRowAsync(HttpClient holder, Guid docId) =>
        (await TestJson.Get(holder, "/api/checkouts")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == docId);

    private static string Rel(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private static async Task<List<int>> WidthsAsync(string url)
    {
        using var http = new HttpClient();
        using var pdf = PdfDocument.Open(await http.GetByteArrayAsync(url));
        return pdf.GetPages().Select(p => (int)p.Width).ToList();
    }

    private static byte[] Pdf(params int[] pageWidths)
    {
        var builder = new PdfDocumentBuilder();
        foreach (var width in pageWidths)
        {
            builder.AddPage(width, 800);
        }

        return builder.Build();
    }
}
