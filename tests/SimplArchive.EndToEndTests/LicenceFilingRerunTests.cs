using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// <c>saconsole module activate</c> can be run again — the same day, and after a run that died half-way (#1613).
/// </summary>
/// <remarks>
/// The licence used to be POSTed unconditionally under a name both kiosk seeds stamp with the day, so a same-day
/// re-run answered <c>409 DOCUMENT_NAME_CONFLICT</c> — and so did every retry after a run whose upload failed, which
/// left the document behind with no content (FlightSchool#72 ended in a full reset). Driven through the CLI's own
/// filing code against the real API and real object storage, because the defect lives in that interaction.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class LicenceFilingRerunTests(E2EApiFactory factory)
{
    [Fact]
    public async Task The_same_licence_twice_files_once_and_a_changed_one_becomes_a_new_version()
    {
        var (api, http, repositoryId) = await ArchiveAsync();
        var name = $"Licence {Guid.NewGuid():N}";
        var first = Encoding.UTF8.GetBytes("""{"licence":"first"}""");

        var filed = await LicenceFiling.FileAsync(api, new Hypermedia(api), repositoryId, name, first, CancellationToken.None);
        Assert.Equal(LicenceFiling.Outcome.Filed, filed.Outcome);

        // The re-run that used to answer 409.
        var again = await LicenceFiling.FileAsync(api, new Hypermedia(api), repositoryId, name, first, CancellationToken.None);
        Assert.Equal((filed.DocumentId, LicenceFiling.Outcome.Unchanged), again);
        Assert.Single(await ConfirmedHashesAsync(http, filed.DocumentId));

        // A renewed licence on the same day is a new VERSION of the same document — its history stays in one place.
        var renewed = Encoding.UTF8.GetBytes("""{"licence":"renewed"}""");
        var next = await LicenceFiling.FileAsync(api, new Hypermedia(api), repositoryId, name, renewed, CancellationToken.None);
        Assert.Equal((filed.DocumentId, LicenceFiling.Outcome.NewVersion), next);
        Assert.Equal(Hash(renewed), (await ConfirmedHashesAsync(http, filed.DocumentId))[^1]);
    }

    [Fact]
    public async Task A_document_left_behind_by_a_failed_upload_is_completed_rather_than_refused()
    {
        var (api, http, repositoryId) = await ArchiveAsync();
        var name = $"Licence {Guid.NewGuid():N}";

        // What a run that died after creating the document leaves: the row, and no content.
        var orphan = await TestJson.Post(http, $"/api/documents/{repositoryId}/children", new { name });
        var orphanId = orphan.GetProperty("id").GetGuid();
        Assert.Empty(await ConfirmedHashesAsync(http, orphanId));

        var licence = Encoding.UTF8.GetBytes("""{"licence":"recovered"}""");
        var recovered = await LicenceFiling.FileAsync(api, new Hypermedia(api), repositoryId, name, licence, CancellationToken.None);

        Assert.Equal((orphanId, LicenceFiling.Outcome.NewVersion), recovered);
        Assert.Equal([Hash(licence)], await ConfirmedHashesAsync(http, orphanId));
    }

    private async Task<(SimplArchiveApi Api, HttpClient Http, Guid RepositoryId)> ArchiveAsync()
    {
        var (clientId, secret, _) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var http = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var repository = await TestJson.Post(http, "/api/repositories", new { name = $"licences-{Guid.NewGuid():N}" });
        return (new SimplArchiveApi(http), http, repository.GetProperty("id").GetGuid());
    }

    private static async Task<List<string?>> ConfirmedHashesAsync(HttpClient http, Guid documentId)
    {
        var versions = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}/versions");
        return versions.GetProperty("versions").EnumerateArray()
            .Where(v => v.GetProperty("status").GetString() == "Confirmed")
            .OrderBy(v => v.GetProperty("versionNumber").GetInt32())
            .Select(v => v.GetProperty("sha256Hash").GetString())
            .ToList();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
