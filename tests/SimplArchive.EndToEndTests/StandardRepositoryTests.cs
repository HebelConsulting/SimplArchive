using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The tenant's standard repository (ADR 0892) over the real wire: provisioning makes the repository it creates the
// standard one and files SimplArchive's own manual into "SimplArchive Manuals" beneath it; the standard repository
// cannot be deleted through the API or a mounted drive; and a tenant administrator changes it with If-Match, which
// moves the manuals folder in the same transaction.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class StandardRepositoryTests
{
    private readonly E2EApiFactory _factory;

    public StandardRepositoryTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Founded(Guid TenantId, Guid RepositoryId, string RepositoryName, string AdminEmail, HttpClient Admin);

    /// <summary>A tenant founded the way an operator founds one — POST /api/tenants as a platform administrator.</summary>
    private async Task<Founded> FoundAsync()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var platform = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = $"std-{suffix}@std.e2e.local";
        var created = await TestJson.Post(platform, "/api/tenants", new
        {
            name = $"Std {suffix}",
            administratorEmail = email,
            administratorDisplayName = "Std Admin",
            repositoryName = $"Archive {suffix}",
        });

        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(
            email, created.GetProperty("tenantAdministrator").GetProperty("password").GetString()!));
        return new Founded(
            created.GetProperty("id").GetGuid(),
            created.GetProperty("repository").GetProperty("id").GetGuid(),
            created.GetProperty("repository").GetProperty("name").GetString()!,
            email,
            admin);
    }

    private async Task<(Guid Id, Guid? ParentId, string Name)> ManualsFolderAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var folder = await db.Documents.AsNoTracking()
            .SingleAsync(d => db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.Documentation));
        return (folder.Id, folder.ParentId, folder.Name);
    }

    private static async Task<HttpResponseMessage> PutStandardAsync(HttpClient admin, Guid repositoryId, string? etag)
    {
        var settings = await TestJson.Get(admin, "/api/tenant-settings");
        var href = settings.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "settings-standard-repository").GetProperty("href").GetString()!;
        var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(new { standardRepositoryId = repositoryId }) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return await admin.SendAsync(request);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("errorCode").GetString()!;

    [Fact]
    public async Task Founding_a_tenant_makes_its_repository_standard_and_files_the_manual_beneath_it()
    {
        var tenant = await FoundAsync();

        var settings = await TestJson.Get(tenant.Admin, "/api/tenant-settings");
        Assert.Equal(tenant.RepositoryId, settings.GetProperty("standardRepositoryId").GetGuid());

        var folder = await ManualsFolderAsync(tenant.TenantId);
        Assert.Equal(tenant.RepositoryId, folder.ParentId);
        Assert.Equal("SimplArchive Manuals", folder.Name);

        var children = await TestJson.Get(tenant.Admin, $"/api/documents/{folder.Id}/children");
        Assert.Contains(children.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "SimplArchive Manual");
    }

    [Fact]
    public async Task The_standard_repository_cannot_be_deleted_through_the_api_or_a_mounted_drive()
    {
        var tenant = await FoundAsync();

        using var get = await tenant.Admin.GetAsync($"/api/documents/{tenant.RepositoryId}");
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{tenant.RepositoryId}");
        delete.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.ToString());
        using var refused = await tenant.Admin.SendAsync(delete);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("STANDARD_REPOSITORY_CANNOT_BE_DELETED", await ErrorCodeAsync(refused));

        // The mounted drive: same refusal, a 409 the operating system can show at least as "cannot be completed".
        var davPassword = (await TestJson.Post(tenant.Admin, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        using var dav = _factory.CreateClient();
        var davDelete = new HttpRequestMessage(HttpMethod.Delete, $"/SimplArchive/{tenant.RepositoryName}");
        davDelete.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{tenant.AdminEmail}:{davPassword}")));
        using var davRefused = await dav.SendAsync(davDelete);
        Assert.Equal(HttpStatusCode.Conflict, davRefused.StatusCode);

        // Still there, in both senses.
        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.GetAsync($"/api/documents/{tenant.RepositoryId}")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_standard_repository_moves_the_manuals_folder_and_honours_if_match()
    {
        var tenant = await FoundAsync();
        var second = (await TestJson.Post(tenant.Admin, "/api/repositories", new { name = $"Second {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        // A stale tag is refused, and nothing moves.
        using (var stale = await PutStandardAsync(tenant.Admin, second, "\"00000000-0000-0000-0000-000000000000\""))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }

        Assert.Equal(tenant.RepositoryId, (await ManualsFolderAsync(tenant.TenantId)).ParentId);

        using var read = await tenant.Admin.GetAsync("/api/tenant-settings");
        using (var moved = await PutStandardAsync(tenant.Admin, second, read.Headers.ETag!.ToString()))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        Assert.Equal(second, (await TestJson.Get(tenant.Admin, "/api/tenant-settings")).GetProperty("standardRepositoryId").GetGuid());
        Assert.Equal(second, (await ManualsFolderAsync(tenant.TenantId)).ParentId);

        // …and the old one is an ordinary repository again: deletable.
        using var get = await tenant.Admin.GetAsync($"/api/documents/{tenant.RepositoryId}");
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{tenant.RepositoryId}");
        delete.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.SendAsync(delete)).StatusCode);
    }

    // Exporting the standard repository carries its manuals folder, and importing that archive must not try to give a
    // second folder the core-owned mask (ADR 0891) — it arrives as an ordinary folder. Found by the web UI suite's
    // export-then-import test, which failed the moment the demo repository gained a manuals folder.
    [Fact]
    public async Task An_imported_copy_of_the_manuals_folder_arrives_as_an_ordinary_folder()
    {
        var tenant = await FoundAsync();
        var zip = await (await tenant.Admin.GetAsync($"/api/documents/{tenant.RepositoryId}/export?versions=all")).Content.ReadAsByteArrayAsync();

        using (var content = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(zip);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(file, "file", "import.zip");
            using var response = await tenant.Admin.PostAsync($"/api/documents/{tenant.RepositoryId}/imports", content);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        // Still exactly one manuals folder, still the original, still directly under the standard repository.
        var folder = await ManualsFolderAsync(tenant.TenantId);
        Assert.Equal(tenant.RepositoryId, folder.ParentId);
    }

    [Fact]
    public async Task Only_a_live_shared_repository_can_become_standard()
    {
        var tenant = await FoundAsync();
        var folder = (await TestJson.Post(tenant.Admin, $"/api/documents/{tenant.RepositoryId}/children", new { name = $"Not a root {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        foreach (var candidate in new[] { folder, Guid.NewGuid() })
        {
            using var read = await tenant.Admin.GetAsync("/api/tenant-settings");
            using var refused = await PutStandardAsync(tenant.Admin, candidate, read.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("STANDARD_REPOSITORY_INVALID", await ErrorCodeAsync(refused));
        }
    }
}
