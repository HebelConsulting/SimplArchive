using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// An export filtered to "the 16th" contains the caller's 16th, not the server's (#1256, ADR 0801).
//
// WHY THIS IS WORSE THAN THE SEARCH VERSION OF THE SAME BUG. Search already answers the caller's day, and both
// clients already DISPLAY the caller's day — so a user sees a document dated 16 July, filters an export to
// 16 July, and the archive silently does not contain it. A missing search hit prompts another search; a missing
// document in an exported archive is found by whoever receives it, if at all. The export succeeds, reports a
// plausible count, and nothing distinguishes "the filter excluded it" from "it was never there".
//
// WHY IT NEEDS AN E2E TEST. The proof has to be that ONE corpus answers TWO ways depending on who asks. A unit
// test of the range arithmetic passes whether or not the controller resolves a zone at all; a test that only
// asserts "the document is in the archive" passes against a filter that is simply permissive. So each case
// below asserts a presence AND a matching absence.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ExportInTheCallersDayTests
{
    private readonly E2EApiFactory _factory;

    public ExportInTheCallersDayTests(E2EApiFactory factory) => _factory = factory;

    // Summer, so Zurich is +02:00 and the shift is unmistakably a DAY change rather than a near miss:
    // 22:30 UTC on the 15th is 00:30 on the 16th in Zurich.
    private const string StoredDate = "2026-07-15";
    private const string StoredTime = "22:30";
    private const string LocalDate = "2026-07-16";
    private const string Zurich = "Europe/Zurich";

    [Fact]
    public async Task A_document_late_on_the_fifteenth_UTC_exports_under_the_sixteenth_in_Zurich()
    {
        using var owner = await ExporterAsync();

        var tag = $"zzx{Guid.NewGuid():N}";
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"tzexport-{tag}" })).GetProperty("id").GetGuid();

        var timedName = $"timed-{tag}";
        var dateOnlyName = $"dateonly-{tag}";
        await CreateDocAsync(owner, repoId, timedName, StoredDate, StoredTime);

        // THE DELIBERATE EXCEPTION, in the same archive so the two cannot be tested apart: a version with no
        // time never was an instant. Somebody chose the 16th; it is the 16th in every zone and must appear in
        // BOTH exports below. Testing only the timed half would let a "convert everything" implementation pass
        // while quietly dropping every date-only document from a boundary-day export.
        await CreateDocAsync(owner, repoId, dateOnlyName, LocalDate, documentTime: null);

        // 1) THE BUG. In Zurich the timed document IS the 16th.
        var inZurich = await ExportNamesAsync(owner, repoId, LocalDate, LocalDate, Zurich);
        Assert.Contains(timedName, inZurich);
        Assert.Contains(dateOnlyName, inZurich);

        // 2) THE CONTROL, and what makes assertion 1 mean something: asked as UTC, the SAME document is not on
        //    the 16th at all. Without this, a filter that had simply stopped filtering would pass.
        var inUtc = await ExportNamesAsync(owner, repoId, LocalDate, LocalDate, zone: null);
        Assert.DoesNotContain(timedName, inUtc);
        Assert.Contains(dateOnlyName, inUtc);   // a date-only version does not move

        // 3) The other direction: in Zurich the timed document is no longer on the 15th. A conversion applied
        //    the wrong way round passes 1 and fails here.
        var fifteenthInZurich = await ExportNamesAsync(owner, repoId, StoredDate, StoredDate, Zurich);
        Assert.DoesNotContain(timedName, fifteenthInZurich);
    }

    [Fact]
    public async Task The_upper_bound_INCLUDES_its_own_day()
    {
        // The off-by-one this shape invites: "to = the 16th" means through the END of the 16th, so a half-open
        // window has to end where that day ends. An exclusive end would silently drop everything filed on the
        // last day of the range — an export that is short by one day, reported as success.
        using var owner = await ExporterAsync();

        var tag = $"zzy{Guid.NewGuid():N}";
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"tzbound-{tag}" })).GetProperty("id").GetGuid();

        // 21:00 UTC on the 16th is 23:00 on the 16th in Zurich — inside the day, and near enough to its end
        // that an exclusive bound excludes it.
        var lateName = $"late-{tag}";
        await CreateDocAsync(owner, repoId, lateName, LocalDate, "21:00");

        var names = await ExportNamesAsync(owner, repoId, LocalDate, LocalDate, Zurich);
        Assert.Contains(lateName, names);
    }

    [Fact]
    public async Task The_manifest_records_which_zone_the_dates_were_read_in()
    {
        // A recorded filter whose meaning depends on who ran the export, with nothing saying which, is a
        // permanent claim nobody can check. The archive has to be self-describing about what was actually asked.
        using var owner = await ExporterAsync();

        var tag = $"zzz{Guid.NewGuid():N}";
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"tzman-{tag}" })).GetProperty("id").GetGuid();

        using var archive = await ExportAsync(owner, repoId, LocalDate, LocalDate, Zurich);
        var manifest = archive.GetEntry("manifest.json")!;
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        var json = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;

        var filters = json.GetProperty("filters");
        Assert.Equal(Zurich, filters.GetProperty("documentDateTimeZone").GetString());

        // And null when nobody said — not the server's zone, which would make the archive claim a zone the
        // caller never chose.
        using var plain = await ExportAsync(owner, repoId, LocalDate, LocalDate, zone: null);
        using var plainReader = new StreamReader(plain.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
        var plainJson = JsonDocument.Parse(await plainReader.ReadToEndAsync()).RootElement;

        Assert.Equal(JsonValueKind.Null, plainJson.GetProperty("filters").GetProperty("documentDateTimeZone").ValueKind);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    // ONE user who both creates the content and exports it: export is gated on the dedicated CanExport right
    // (not tenant-admin), and a caller exporting somebody else's repository would also need an ACL grant —
    // which is a different feature and not what these tests are about.
    //
    // A USER rather than a service account, because CanExport lives on a user. It also means the header is what
    // supplies the zone here: these users set no preference. That the stored preference BEATS the header is
    // asserted once, in SearchInTheCallersDayTests, against the same resolver both paths now share (#1256) —
    // duplicating it here would pin one rule in two places and invite them to drift apart.
    private async Task<HttpClient> ExporterAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"tzexp-{Guid.NewGuid():N}@e2e.local";
        const string password = "tzexp-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Exporter", canExport: true, canManageRepositories: true);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
    }

    private static async Task CreateDocAsync(
        HttpClient owner, Guid parentId, string docName, string documentDate, string? documentTime)
    {
        var docId = (await TestJson.Post(owner, $"/api/documents/{parentId}/children", new { name = docName })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes($"marker {docName}")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/document-date",
            new { documentDate, documentTime })).EnsureSuccessStatusCode();
    }

    private static async Task<ZipArchive> ExportAsync(
        HttpClient owner, Guid repoId, string from, string to, string? zone)
    {
        var url = $"/api/documents/{repoId}/export?documentDateFrom={from}&documentDateTo={to}";

        // The zone rides as the header both clients set on every request. Sent per call here because the point
        // of these tests is that the SAME corpus answers differently depending on who is asking.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (zone is not null)
        {
            request.Headers.Add("X-Time-Zone", zone);
        }

        var response = await owner.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // Copied to a seekable stream: ZipArchive cannot read the central directory off a forward-only one.
        var buffer = new MemoryStream();
        await (await response.Content.ReadAsStreamAsync()).CopyToAsync(buffer);
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static async Task<HashSet<string>> ExportNamesAsync(
        HttpClient owner, Guid repoId, string from, string to, string? zone)
    {
        using var archive = await ExportAsync(owner, repoId, from, to, zone);
        using var reader = new StreamReader(archive.GetEntry("tree/documents.jsonl")!.Open(), Encoding.UTF8);

        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0 && JsonDocument.Parse(line).RootElement.TryGetProperty("name", out var name))
            {
                names.Add(name.GetString() ?? string.Empty);
            }
        }

        return names;
    }
}
