using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.EndToEndTests;

// Split / join / sort of staged intray items (#487, ADR 0575), driven the way a conforming client must: every
// action is reached by FOLLOWING the rel the server advertised (ADR 0543), so these tests also prove the rels
// exist and reach something.
//
// The pages are given DIFFERENT WIDTHS, which is what makes the assertions real: a count is satisfied by an
// implementation that returns three copies of page one, and "sorted" is satisfied by an implementation that
// sorts nothing. Reading the widths back out of the resulting PDF says which page actually went where.
[Collection(E2ECollection.Name)]
public class IntrayPageOperationsTests
{
    private readonly E2EApiFactory _factory;

    public IntrayPageOperationsTests(E2EApiFactory factory) => _factory = factory;

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
        var intray = await TestJson.Get(client, "/api/intray");
        var join = Rel(intray, "join");

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
    public async Task A_file_with_no_pages_advertises_nothing_and_a_single_page_still_offers_sort()
    {
        var (client, _) = await SignedInUserAsync();

        var text = await StageAsync(client, Encoding.UTF8.GetBytes("just a note"), ".txt");
        Assert.DoesNotContain("pages", (await ItemLinksAsync(client, text)).Select(l => l.Rel));

        var single = await StageAsync(client, Pdf(300));
        var pages = await FollowAsync(client, await ItemLinksAsync(client, single), "pages");
        Assert.Equal(1, pages.GetProperty("pageCount").GetInt32());

        // A single page can't be split — but it CAN be rotated, and rotation rides the sort request, so a
        // one-page file advertises `sort` (#549: an upside-down one-page scan is exactly the rotate case).
        var rels = pages.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.DoesNotContain("split", rels);
        Assert.Contains("sort", rels);
    }

    // Leaving a page OUT of the order deletes it — how the sort dialog's bin button removes a blank back or a
    // separator sheet. It is the one operation that destroys content, which is why the clients confirm the
    // count first: replacing in place leaves nothing to undo it.
    [Fact]
    public async Task Leaving_a_page_out_of_the_order_deletes_it()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400, 500));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        (await client.PostAsJsonAsync(Rel(pages, "sort"), new { pageOrder = new[] { 3, 1 } })).EnsureSuccessStatusCode();

        // The middle page is gone, the survivors are in the order asked for, and there is no second item left
        // behind — a sort still produces exactly one document.
        Assert.Equal([500, 300], await PageWidthsAsync(client, name));
        Assert.Single(await NamesAsync(client), n => n == name);
    }

    [Fact]
    public async Task A_sort_that_would_duplicate_a_page_is_refused_and_changes_nothing()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400, 500));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        var sort = Rel(pages, "sort");

        // Page 2 listed TWICE. Dropping a page is now legitimate, but duplicating one is not a choice anybody
        // makes on purpose — and it is refused before anything is written, because the sort replaces in place.
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

        var join = Rel(await TestJson.Get(client, "/api/intray"), "join");

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

    // Stages bytes in the intray the way a client does: ask for an upload URL, then PUT straight to storage (the
    // Api never proxies file bytes).
    private static async Task<string> StageAsync(HttpClient client, byte[] bytes, string extension = ".pdf")
    {
        var name = $"scan-{Guid.NewGuid():N}{extension}";
        var upload = await TestJson.Post(client, "/api/intray", new { fileName = name });

        using var storage = new HttpClient();
        (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes)))
            .EnsureSuccessStatusCode();

        return name;
    }

    private static async Task<List<string>> NamesAsync(HttpClient client) =>
        (await TestJson.Get(client, "/api/intray")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).ToList();

    private static async Task<List<(string Rel, string Href)>> ItemLinksAsync(HttpClient client, string name)
    {
        var item = (await TestJson.Get(client, "/api/intray")).GetProperty("items").EnumerateArray()
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

    // ---- Rotation (#522): the sort request may turn individual pages while reordering. ------------------

    // A PDF page rotates LOSSLESSLY — the /Rotate attribute composes, the content stream is untouched — so
    // the widths that identify pages still read back unchanged, and turning an already-turned page adds up.
    [Fact]
    public async Task Rotating_a_pdf_page_composes_the_rotate_attribute_and_touches_nothing_else()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        var sort = Rel(pages, "sort");

        (await client.PostAsJsonAsync(sort, new
        {
            pageOrder = new[] { 2, 1 },
            rotations = new[] { new { page = 2, degrees = 90 } },
        })).EnsureSuccessStatusCode();

        // PdfPig reads Width ROTATION-AWARE: the 400x800 page turned 90 now reads 800 — which is itself
        // proof the /Rotate landed, while the unrotated sibling's 300 proves the reorder put it second.
        Assert.Equal([800, 300], await PageWidthsAsync(client, name));
        Assert.Equal([90, 0], await PageRotationsAsync(client, name));

        // Turn the same (now first) page again: 90 + 90 composes to 180 rather than resetting.
        var again = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        (await client.PostAsJsonAsync(Rel(again, "sort"), new
        {
            pageOrder = new[] { 1, 2 },
            rotations = new[] { new { page = 1, degrees = 90 } },
        })).EnsureSuccessStatusCode();

        Assert.Equal([180, 0], await PageRotationsAsync(client, name));
        Assert.Equal([400, 300], await PageWidthsAsync(client, name)); // 180 restores the portrait reading
    }

    // A TIFF page has no /Rotate, so rotation re-encodes it — the same trade its deskew already makes. A
    // quarter turn must swap the page's dimensions, and the other page must keep its own.
    [Fact]
    public async Task Rotating_a_tiff_page_swaps_its_dimensions()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Tiff((300, 500), (400, 500)), ".tif");

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        (await client.PostAsJsonAsync(Rel(pages, "sort"), new
        {
            pageOrder = new[] { 1, 2 },
            rotations = new[] { new { page = 1, degrees = 90 } },
        })).EnsureSuccessStatusCode();

        // Joined TIFF pages share a canvas (the join pads to the largest), so the rotated page's 500x300 is
        // padded to the un-rotated sibling's 400x500 → the common canvas is 500x500. What proves the turn is
        // the canvas WIDTH: without the rotation it would be max(300, 400) = 400; the turn makes it 500.
        var download = (await ItemLinksAsync(client, name)).Single(l => l.Rel == "download").Href;
        using var storage = new HttpClient();
        var bytes = await storage.GetByteArrayAsync(download);
        using var image = NetVips.Image.NewFromBuffer(bytes);
        Assert.Equal(500, image.Width);
    }

    [Fact]
    public async Task A_rotation_that_is_not_a_quarter_turn_or_names_a_dropped_page_is_refused()
    {
        var (client, _) = await SignedInUserAsync();
        var name = await StageAsync(client, Pdf(300, 400));

        var pages = await FollowAsync(client, await ItemLinksAsync(client, name), "pages");
        var sort = Rel(pages, "sort");

        var crooked = await client.PostAsJsonAsync(sort, new
        {
            pageOrder = new[] { 1, 2 },
            rotations = new[] { new { page = 1, degrees = 45 } },
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, crooked.StatusCode);

        var dropped = await client.PostAsJsonAsync(sort, new
        {
            pageOrder = new[] { 1 },
            rotations = new[] { new { page = 2, degrees = 90 } },
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, dropped.StatusCode);

        // Refused before anything was written — both pages still there, unrotated.
        Assert.Equal([300, 400], await PageWidthsAsync(client, name));
        Assert.Equal([0, 0], await PageRotationsAsync(client, name));
    }

    private static async Task<List<int>> PageRotationsAsync(HttpClient client, string name)
    {
        var download = (await ItemLinksAsync(client, name)).Single(l => l.Rel == "download").Href;

        using var storage = new HttpClient();
        var bytes = await storage.GetByteArrayAsync(download);

        using var document = PdfDocument.Open(bytes);
        return document.GetPages().Select(p => p.Rotation.Value).ToList();
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

    // A multi-page TIFF with per-page dimensions — NetVips writes pages as one tall strip + a page height,
    // and mixed sizes are padded to the largest (the same behaviour PageComposer's join documents).
    private static byte[] Tiff(params (int Width, int Height)[] pages)
    {
        var images = pages.Select(p => (NetVips.Image.Black(p.Width, p.Height) + 255).Cast(NetVips.Enums.BandFormat.Uchar)).ToArray();
        try
        {
            var width = images.Max(i => i.Width);
            var height = images.Max(i => i.Height);
            var normalised = images
                .Select(i => i.Width == width && i.Height == height ? i : i.Gravity(NetVips.Enums.CompassDirection.Centre, width, height))
                .ToArray();
            using var joined = NetVips.Image.Arrayjoin(normalised, across: 1);
            return joined.WriteToBuffer(".tif", new NetVips.VOption { { "page_height", height } });
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }
}
