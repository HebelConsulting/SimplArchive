using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API (in-process) + real Postgres + MinIO + Gotenberg, exercising the preview
// rendition pipeline (ADR "Server-side preview renditions" 0226, "Office document preview via Gotenberg" 0228,
// "CSV/Markdown preview" 0232, "Preview fallback when a rendition can't be produced" 0229). The single-version
// resource builds the `preview` link on demand — GETting it triggers the actual conversion — so no polling is
// needed (renditions are synchronous, unlike search indexing). TIFF→PNG is intentionally not covered: NetVips'
// native lib is only packaged for the Linux-musl container image, not this macOS test host.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class PreviewRenditionTests
{
    private readonly E2EApiFactory _factory;

    public PreviewRenditionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Preview_renditions_over_real_gotenberg()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"preview-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // 1) Office → PDF via the LibreOffice route — a .csv is plain text but in the office family, so it
        //    exercises the LibreOffice conversion without needing a binary .docx.
        var csv = await UploadAndFetchVersionAsync(owner, repoId, "sheet", ".csv", Encoding.UTF8.GetBytes("name,amount\nInvoice,42\n"));
        Assert.True(csv.GetProperty("previewConverted").GetBoolean());
        Assert.True(IsPdf(await FetchAsync(PreviewLink(csv)!)));

        // 2) Markdown → PDF via the Chromium route (the second, distinct Gotenberg route).
        var md = await UploadAndFetchVersionAsync(owner, repoId, "notes", ".md", Encoding.UTF8.GetBytes("# Title\n\nHello **world**.\n"));
        Assert.True(md.GetProperty("previewConverted").GetBoolean());
        Assert.True(IsPdf(await FetchAsync(PreviewLink(md)!)));

        // 3) JSON → pretty-printed text — done in-process (System.Text.Json), so it also proves the
        //    non-Gotenberg rendition branch. The rendition is the re-indented source.
        var json = await UploadAndFetchVersionAsync(owner, repoId, "data", ".json", Encoding.UTF8.GetBytes("{\"marker\":\"zzyzx\",\"nested\":{\"a\":1}}"));
        Assert.True(json.GetProperty("previewConverted").GetBoolean());
        var jsonText = Encoding.UTF8.GetString(await FetchAsync(PreviewLink(json)!));
        Assert.Contains("zzyzx", jsonText);
        Assert.Contains("\n", jsonText); // re-indented, not the single-line original

        // 4) Served-as-is original — a .pdf is browser-viewable, so the preview link points at the original
        //    itself (no rendition) and previewConverted is false.
        var pdf = await UploadAndFetchVersionAsync(owner, repoId, "doc", ".pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        Assert.False(pdf.GetProperty("previewConverted").GetBoolean());
        Assert.True(IsPdf(await FetchAsync(PreviewLink(pdf)!)));

        // 5) Conversion failure → no preview link. The ZIP magic (PK\x03\x04) with a corrupt body steers
        //    LibreOffice to the OOXML (zip) import filter, which rejects the bad archive (a plain-text .docx
        //    would instead succeed via LibreOffice's text fallback), so Gotenberg 400s → the rendition fails →
        //    the resource omits the `preview` link (the client's "No preview available" path, ADR 0229) rather
        //    than 500ing.
        byte[] corruptDocx = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes("corrupt-zip-body-not-a-valid-docx")];
        var broken = await UploadAndFetchVersionAsync(owner, repoId, "broken", ".docx", corruptDocx);
        Assert.Null(PreviewLink(broken));
    }

    // A contact card and an appointment render as CARDS (ADR 0651), reached the way a reader reaches them: by
    // following a VERSION's `preview` rel. That rel is one dispatch for the whole archive rather than a
    // per-tab list, so this is also the answer for the Repositories tab — an addressbook is an ordinary node in
    // the repository tree, and a card selected there previews through exactly this path. Before this, both
    // formats answered "No preview available": the only ones in the archive with nothing to show.
    //
    // Filed into an ADDRESSBOOK and a CALENDAR rather than a plain folder, because that is the only place they
    // can be: the classifier stamps an uploaded .vcf as a Contact, and typed-folder containment admits a
    // Contact nowhere else. Uploading one into an ordinary folder fails at finalize — pre-existing, on merged
    // main, and filed separately rather than fixed in passing.
    [Fact]
    public async Task A_contact_card_and_an_appointment_preview_as_rendered_cards()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"cards-{Guid.NewGuid():N}@e2e.local";
        const string password = "cards-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Card Reader");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var children = (await TestJson.Get(api, $"/api/documents/{personalId}/children")).GetProperty("children").EnumerateArray().ToList();
        Guid IdOf(string name) => children.Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

        var vcard = Encoding.UTF8.GetBytes(
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:e2e-1\r\nFN:Ada Lovelace\r\nORG:Northwind Trading\r\n"
            + "EMAIL;TYPE=work:ada@northwind.example\r\nEND:VCARD\r\n");

        var contact = await UploadAndFetchVersionAsync(api, IdOf("My Addressbook"), "ada", ".vcf", vcard);
        Assert.True(contact.GetProperty("previewConverted").GetBoolean());
        Assert.True(IsPdf(await FetchAsync(PreviewLink(contact)!)));

        var ics = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//e2e//EN\r\nBEGIN:VEVENT\r\nUID:e2e-2\r\n"
            + "SUMMARY:The Iron Horse\r\nDTSTART:20260829T190000\r\nDTEND:20260829T210000\r\n"
            + "LOCATION:Northampton, MA\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        var appointment = await UploadAndFetchVersionAsync(api, IdOf("My Calendar"), "gig", ".ics", ics);
        Assert.True(appointment.GetProperty("previewConverted").GetBoolean());
        Assert.True(IsPdf(await FetchAsync(PreviewLink(appointment)!)));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<System.Text.Json.JsonElement> UploadAndFetchVersionAsync(HttpClient owner, Guid repoId, string docName, string fileExtension, byte[] content)
    {
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = docName })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension });
        var versionId = created.GetProperty("id").GetGuid();

        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(content))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        // GETting the single-version resource triggers on-demand rendition generation.
        return await TestJson.Get(owner, $"/api/documents/{docId}/versions/{versionId}");
    }

    private static string? PreviewLink(System.Text.Json.JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == "preview")
            .Select(l => l.GetProperty("href").GetString())
            .FirstOrDefault();

    private static async Task<byte[]> FetchAsync(string url)
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url);
    }

    private static bool IsPdf(byte[] bytes) => bytes.Length >= 4 && Encoding.ASCII.GetString(bytes, 0, 4) == "%PDF";
}
