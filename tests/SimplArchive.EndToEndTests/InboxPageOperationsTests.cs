using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.EndToEndTests;

// Split / join / sort of staged inbox items (#487, ADR 0575), driven the way a conforming client must: every
// action is reached by FOLLOWING the rel the server advertised (ADR 0543), so these tests also prove the rels
// exist and reach something.
//
// The pages are given DIFFERENT WIDTHS, which is what makes the assertions real: a count is satisfied by an
// implementation that returns three copies of page one, and "sorted" is satisfied by an implementation that
// sorts nothing. Reading the widths back out of the resulting PDF says which page actually went where.
[Collection(E2ECollection.Name)]
public class InboxPageOperationsTests
{
    private readonly E2EApiFactory _factory;

    public InboxPageOperationsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Splitting_makes_one_item_per_page_and_keeps_the_original()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400, 500));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        Assert.Equal(3, pages.GetProperty("pageCount").GetInt32());
        Assert.Equal("pdf", pages.GetProperty("format").GetString());

        var split = Rel(pages, "split");
        var written = (await TestJson.Post(client, split, new { })).GetProperty("names").EnumerateArray()
            .Select(n => n.GetString()!).ToList();

        Assert.Equal(3, written.Count);

        // The source is still there. A scan can be the only copy of a piece of paper, so an operation that turns
        // out to be wrong has to be survivable by deleting its output rather than by re-scanning.
        var listed = await NamesAsync(client);
        Assert.Contains(name, listed);
        Assert.All(written, w => Assert.Contains(w, listed));

        // Each piece is one page, and they are the ORIGINAL pages in order — the widths say so.
        for (var i = 0; i < written.Count; i++)
        {
            var piece = await FollowAsync(client, await ItemLinksAsync(client, written[i]), "pages");
            Assert.Equal(1, piece.GetProperty("pageCount").GetInt32());
            Assert.Equal([300 + (i * 100)], await PageWidthsAsync(client, written[i]));
        }
    }

    [Fact]
    public async Task Joining_concatenates_the_items_in_the_order_given_and_keeps_them()
    {
        var (client, _) = await SignedInUserAsync();
        var first = await StageAsync(client, Pdf(300, 400));
        var second = await StageAsync(client, Pdf(500));

        // The collection's own action, advertised where the collection is read (ADR 0557).
        var inbox = await TestJson.Get(client, "/api/inbox");
        var join = Rel(inbox, "join");

        var target = $"joined-{Guid.NewGuid():N}.pdf";
        var joined = (await TestJson.Post(client, join, new { names = new[] { second, first }, name = target }))
            .GetProperty("names").EnumerateArray().Single().GetString()!;

        // Second first, because that is the order asked for — a join that sorted its inputs would give 300/400/500.
        Assert.Equal([500, 300, 400], await PageWidthsAsync(client, joined));

        var listed = await NamesAsync(client);
        Assert.Contains(first, listed);
        Assert.Contains(second, listed);
    }

    [Fact]
    public async Task Sorting_rewrites_the_same_item_in_the_given_order()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400, 500));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        var sort = Rel(pages, "sort");

        (await client.PostAsJsonAsync(sort, new { pageOrder = new[] { 3, 1, 2 } })).EnsureSuccessStatusCode();

        // Same item, same page count, new order — and no second copy left behind to clean up.
        Assert.Equal([500, 300, 400], await PageWidthsAsync(client, name));
        Assert.Contains(name, await NamesAsync(client));
    }

    // ADR 0554: an action that cannot succeed is not advertised. These are the two shapes where that bites — a
    // format with no page sequence at all, and a file that has pages but only one.
    [Fact]
    public async Task A_file_with_no_pages_to_operate_on_advertises_no_page_operations()
    {
        var (client, _) = await SignedInUserAsync();

        var text = await StageAsync(client, Encoding.UTF8.GetBytes("just a note"), ".txt");
        Assert.DoesNotContain("pages", (await ItemLinksAsync(client, text)).Select(l => l.Rel));

        var single = await StageAsync(client, Pdf(300));
        var pages = await FollowAsync(client, await ItemLinksAsync(client, single), "pages");
        Assert.Equal(1, pages.GetProperty("pageCount").GetInt32());

        var rels = pages.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.DoesNotContain("split", rels);
        Assert.DoesNotContain("sort", rels);
    }

    [Fact]
    public async Task A_sort_that_would_lose_a_page_is_refused_and_changes_nothing()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400, 500));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        var sort = Rel(pages, "sort");

        // Page 2 listed twice, page 3 not at all. Refused BEFORE anything is written — which is the only reason
        // sorting is allowed to replace its source at all.
        var response = await client.PostAsJsonAsync(sort, new { pageOrder = new[] { 1, 2, 2 } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal([300, 400, 500], await PageWidthsAsync(client, name));
    }

    [Fact]
    public async Task Joining_across_formats_is_refused_rather_than_silently_converting()
    {
        var (client, _) = await SignedInUserAsync();
        var pdf = await StageAsync(client, Pdf(300, 400));
        var text = await StageAsync(client, Encoding.UTF8.GetBytes("not a scan"), ".txt");

        var join = Rel(await TestJson.Get(client, "/api/inbox"), "join");

        var response = await client.PostAsJsonAsync(join, new { names = new[] { pdf, text } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(HttpClient Client, Guid TenantId)> SignedInUserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"pages-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Pager", canManageRepositories: true);
        return (_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234")), tenantId);
    }

    // Stages bytes in the inbox the way a client does: ask for an upload URL, then PUT straight to storage (the
    // Api never proxies file bytes).
    private static async Task<string> StageAsync(HttpClient client, byte[] bytes, string extension = ".pdf")
    {
        var name = $"scan-{Guid.NewGuid():N}{extension}";
        var upload = await TestJson.Post(client, "/api/inbox", new { fileName = name });

        using var storage = new HttpClient();
        (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes)))
            .EnsureSuccessStatusCode();

        return name;
    }

    private static async Task<List<string>> NamesAsync(HttpClient client) =>
        (await TestJson.Get(client, "/api/inbox")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).ToList();

    private static async Task<List<(string Rel, string Href)>> ItemLinksAsync(HttpClient client, string name)
    {
        var item = (await TestJson.Get(client, "/api/inbox")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == name);

        return item.GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("rel").GetString()!, l.GetProperty("href").GetString()!))
            .ToList();
    }

    private static async Task<JsonElement> FollowAsync(HttpClient client, List<(string Rel, string Href)> links, string rel) =>
        await TestJson.Get(client, links.Single(l => l.Rel == rel).Href);

    private static string Rel(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    // Downloads the item and reads its page widths — the identity trick that lets order be asserted at all.
    private static async Task<List<int>> PageWidthsAsync(HttpClient client, string name)
    {
        var download = (await ItemLinksAsync(client, name)).Single(l => l.Rel == "download").Href;

        using var storage = new HttpClient();
        var bytes = await storage.GetByteArrayAsync(download);

        using var document = PdfDocument.Open(bytes);
        return document.GetPages().Select(p => (int)Math.Round(p.Width)).ToList();
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
