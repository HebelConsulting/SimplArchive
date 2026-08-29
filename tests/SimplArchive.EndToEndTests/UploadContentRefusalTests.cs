using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// What the archive refuses to store, over the real API (ADR 0718, issue #846). ADR 0123 named this interim
// mitigation and it was never built: an .exe was accepted and stored, and nothing looked at what arrived.
//
// Checked at FINALIZE, which is the one place every upload path reaches — the versions endpoint, check-in,
// intray filing, WebDAV and the protocol edges. A rule written at each entrance is silently absent at the
// entrance nobody remembered.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class UploadContentRefusalTests
{
    private readonly E2EApiFactory _factory;

    public UploadContentRefusalTests(E2EApiFactory factory) => _factory = factory;

    private static readonly byte[] WindowsExecutable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

    private async Task<(HttpClient Api, Guid Repo)> SetupAsync()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repo = (await TestJson.Post(api, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();

        return (api, repo);
    }

    /// <summary>Uploads the bytes and returns the FINALIZE response — the moment the content is judged.</summary>
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient api, Guid folderId, string extension, byte[] content)
    {
        var doc = await TestJson.Post(api, $"/api/documents/{folderId}/children", new { name = $"d{Guid.NewGuid():N}"[..9] });
        var docId = doc.GetProperty("id").GetGuid();

        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = extension });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(content)))
                .EnsureSuccessStatusCode();
        }

        return await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
    }

    [Fact]
    public async Task An_executable_is_refused_however_it_is_named()
    {
        var (api, repo) = await SetupAsync();
        using var _1 = api;

        // By its name…
        var byName = await UploadAsync(api, repo, ".exe", Encoding.ASCII.GetBytes("not really a binary"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, byName.StatusCode);
        Assert.Contains("UNSUPPORTED_UPLOAD_CONTENT", await byName.Content.ReadAsStringAsync());

        // …and by its BYTES, which is the case that matters: a program wearing a document's name is the
        // disguise, and it is the one an extension list alone would wave through.
        var byContent = await UploadAsync(api, repo, ".pdf", WindowsExecutable);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, byContent.StatusCode);

        // A script has no signature to find — it is plain text — so the name is all there is to judge it by.
        var script = await UploadAsync(api, repo, ".ps1", Encoding.ASCII.GetBytes("Write-Host hello"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, script.StatusCode);
    }

    [Fact]
    public async Task An_ordinary_document_is_unaffected()
    {
        var (api, repo) = await SetupAsync();
        using var _1 = api;

        // The control. A guard whose refusals are never contrasted with an acceptance is a guard that might be
        // refusing everything.
        var pdf = await UploadAsync(api, repo, ".pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nnot a real pdf, but not a program either"));
        Assert.True(pdf.IsSuccessStatusCode, $"finalize answered {(int)pdf.StatusCode}");

        // …including the formats an allowlist of "document-ish" types would have refused. This is why the rule
        // is a blocklist: an archive that will not keep the customer's CAD file has failed at its job.
        var archive = await UploadAsync(api, repo, ".zip", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);
        Assert.True(archive.IsSuccessStatusCode, $"finalize answered {(int)archive.StatusCode}");
    }

    [Fact]
    public async Task An_email_keeps_its_place_when_an_attachment_is_refused()
    {
        var (api, repo) = await SetupAsync();
        using var _1 = api;

        var docId = (await TestJson.Post(api, $"/api/documents/{repo}/children", new { name = $"m{Guid.NewGuid():N}"[..9] }))
            .GetProperty("id").GetGuid();
        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".eml" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(BuildEmail())))
                .EnsureSuccessStatusCode();
        }

        var finalize = await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });

        // The MESSAGE is archived. Refusing the whole email would lose a business record because of one
        // attachment, and the sender gets no useful signal from it either.
        Assert.True(finalize.IsSuccessStatusCode, $"finalize answered {(int)finalize.StatusCode}");

        // The harmless attachment was filed as a child; the executable was not.
        var children = await TestJson.Get(api, $"/api/documents/{docId}/children");
        var names = children.GetProperty("children").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains(names, n => n!.Contains("notes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n!.Contains("invoice", StringComparison.OrdinalIgnoreCase));

        // And the drop is SAID, in the email's own thread — where the person reading the message is. The entry
        // carries the file name; the sentence around it is the client's to compose (kind 3, ADR 0718).
        var chat = await TestJson.Get(api, $"/api/documents/{docId}/chat");
        var refused = chat.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("kind").GetInt32() == 3).ToList();
        var entry = Assert.Single(refused);
        Assert.Contains("invoice", entry.GetProperty("body").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildEmail() => Encoding.ASCII.GetBytes(
        "From: alice@example.com\r\n" +
        "To: bob@example.com\r\n" +
        "Subject: Quarterly figures\r\n" +
        "Date: Mon, 01 Jan 2024 10:00:00 +0000\r\n" +
        $"Message-ID: <{Guid.NewGuid():N}@example.com>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
        "\r\n" +
        "--B\r\n" +
        "Content-Type: text/plain\r\n" +
        "\r\n" +
        "See attached.\r\n" +
        "\r\n" +
        "--B\r\n" +
        "Content-Type: text/plain; name=\"notes.txt\"\r\n" +
        "Content-Disposition: attachment; filename=\"notes.txt\"\r\n" +
        "\r\n" +
        "harmless\r\n" +
        "\r\n" +
        "--B\r\n" +
        "Content-Type: application/octet-stream; name=\"invoice.exe\"\r\n" +
        "Content-Disposition: attachment; filename=\"invoice.exe\"\r\n" +
        "\r\n" +
        "MZ this is a program\r\n" +
        "--B--\r\n");
}
