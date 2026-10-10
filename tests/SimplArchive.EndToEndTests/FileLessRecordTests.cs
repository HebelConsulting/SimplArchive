using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// A file-less RECORD (a document whose mask is not a folder mask, carrying only index data: a push key, a
/// credential) is listed as a document, not a folder (#1708). Every client inferred "folder" from "has no file", so
/// such a record opened as an empty folder: its index data never showed, and the desktop kept reloading it.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class FileLessRecordTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_file_less_record_is_listed_as_a_document_and_a_folder_as_a_folder()
    {
        var (clientId, secret, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var repositoryId = (await TestJson.Post(api, "/api/repositories", new { name = $"Records {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(api, $"/api/documents/{repositoryId}/children", new { name = "A folder" })).GetProperty("id").GetGuid();

        Guid recordId;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var record = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = repositoryId,
                Name = "Push key 2026-10-08",
                MaskVersionId = await db.MaskVersions.Where(mv => mv.MaskId == WellKnownMaskIds.BasicEntry && mv.IsCurrent).Select(mv => mv.Id).SingleAsync(),
                CreatedByServiceAccountId = await db.Documents.Where(d => d.Id == repositoryId).Select(d => d.CreatedByServiceAccountId).SingleAsync(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(record);
            await db.SaveChangesAsync();
            recordId = record.Id;
        }

        static JsonElement Row(JsonElement listing, string property, Guid id) =>
            listing.GetProperty(property).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

        var children = await TestJson.Get(api, $"/api/documents/{repositoryId}/children");
        Assert.False(Row(children, "children", recordId).GetProperty("hasVersions").GetBoolean());
        Assert.False(Row(children, "children", recordId).GetProperty("isFolder").GetBoolean());
        Assert.True(Row(children, "children", folderId).GetProperty("isFolder").GetBoolean());

        var repositoryDocuments = await TestJson.Get(api, $"/api/repositories/{repositoryId}/documents");
        Assert.False(Row(repositoryDocuments, "documents", recordId).GetProperty("isFolder").GetBoolean());
        Assert.True(Row(repositoryDocuments, "documents", folderId).GetProperty("isFolder").GetBoolean());

        var repositories = await TestJson.Get(api, "/api/repositories");
        Assert.True(Row(repositories, "repositories", repositoryId).GetProperty("isFolder").GetBoolean());
    }
}
