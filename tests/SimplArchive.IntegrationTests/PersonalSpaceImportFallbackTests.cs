using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Importing an archive whose personal space holds things this tenant's first level will not (#630).
//
// The archive is HAND-WRITTEN rather than produced by exporting a seeded tenant, and that is not a shortcut —
// the shape cannot be built any more. `SaveChanges` refuses a Notebook at a personal space's first level, so
// the only way to have one is to have made it before the rule existed. Which is exactly the real case:
// `Personal/Notebook` was PROVISIONED until 2026-08-19, so every archive exported before that date carries one
// there, and importing it hit a hard refusal until this fallback existed.
public class PersonalSpaceImportFallbackTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>An archive shaped like a personal space from before the first level was closed.</summary>
    private static MemoryStream ArchiveWithLegacyPersonalSpace(Guid ownerId, string ownerEmail)
    {
        var rootId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var looseId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        // The masks the archive's own documents wear. Well-known, so the importer maps them onto this tenant's
        // copies by id rather than minting duplicates.
        var notebookVersionId = Guid.NewGuid();
        var folderVersionId = Guid.NewGuid();
        var entryVersionId = Guid.NewGuid();

        string Mask(Guid maskId, Guid versionId, string name) =>
            $$"""{"maskId":"{{maskId}}","wellKnown":true,"version":{"maskVersionId":"{{versionId}}","name":"{{name}}","versionNumber":1,"reviewSlaDays":null,"retentionYears":null,"defaultSensitivityLabel":null},"fields":[]}""";

        string Doc(Guid id, Guid? parentId, string name, Guid? maskVersionId, Guid? personalOf = null) =>
            JsonSerializer.Serialize(new
            {
                id,
                parentId,
                name,
                maskVersionId,
                sensitivityLabel = (string?)null,
                createdByUserId = ownerId,
                createdByServiceAccountId = (Guid?)null,
                createdAt = DateTimeOffset.UtcNow,
                breaksInheritance = false,
                personalOfUserId = personalOf,
            }, Json);

        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.json", JsonSerializer.Serialize(new
            {
                formatVersion = 2,
                source = new { tenantId = Guid.NewGuid(), tenantName = "Older" },
                root = new { documentId = rootId, name = "Personal" },
            }, Json));

            Write(zip, "principals/principals.json", JsonSerializer.Serialize(new
            {
                users = new[] { new { id = ownerId, email = ownerEmail, displayName = "Owner", isActive = true, clearanceRank = (int?)null } },
                serviceAccounts = Array.Empty<object>(),
                groups = Array.Empty<object>(),
                memberships = Array.Empty<object>(),
            }, Json));

            Write(zip, "masks/masks.json",
                $"[{Mask(WellKnownMaskIds.Notebook, notebookVersionId, "Notebook")},"
                + $"{Mask(WellKnownMaskIds.Folder, folderVersionId, "Folder")},"
                + $"{Mask(WellKnownMaskIds.BasicEntry, entryVersionId, "Basic Entry")}]");

            // Personal ─┬─ Notebook      ← may now live only under a Mailbox
            //           ├─ Loose report  ← an item, which the first level no longer holds
            //           └─ Tax 2026      ← a plain folder, which it no longer takes either (#634)
            Write(zip, "tree/documents.jsonl", string.Join('\n',
                Doc(rootId, null, "Personal", null, ownerId),
                Doc(notebookId, rootId, "Notebook", notebookVersionId),
                Doc(looseId, rootId, "Loose report", entryVersionId),
                Doc(folderId, rootId, "Tax 2026", folderVersionId)));

            Write(zip, "tree/versions.jsonl", string.Empty);
            Write(zip, "tree/index-data.jsonl", string.Empty);
        }

        buffer.Position = 0;
        return buffer;
    }

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId, string Email)> TenantAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        var email = "owner@t.test";
        Guid userId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = email, DisplayName = "Owner", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        return (connection, accessor, userId, email);
    }

    private RepositoryImporter Importer(SimplArchiveDbContext db, InMemoryObjectStorage storage, CurrentTenantAccessor accessor) =>
        new(db, storage, accessor,
            new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance),
            new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance),
            NoOpDocumentIndexQueue.Instance, NoOpSearchablePdfQueue.Instance,
            new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance),
            new PersonalMailboxProvisioner(db, new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance)),
            NullLogger<RepositoryImporter>.Instance);

    [Fact]
    public async Task A_legacy_personal_space_imports_with_its_contents_re_parented()
    {
        var (connection, accessor, _, email) = await TenantAsync();
        using var _c = connection;

        var storage = new InMemoryObjectStorage();
        RepositoryImporter.ImportResult result;
        using (var db = Ctx(connection, accessor))
        {
            var archive = ArchiveWithLegacyPersonalSpace(Guid.NewGuid(), email);
            result = await Importer(db, storage, accessor).ImportAsync(
                archive, targetFolderId: null, updateExisting: false, includePermissions: false,
                merge: false, LeafMergeMode.Rename, CancellationToken.None);
        }

        // It imported at all — which before the fallback it did not: the Notebook's mask assignment was refused
        // and took the whole import down with it.
        Assert.True(result.Relocated > 0);

        using var check = Ctx(connection, accessor);
        var personal = await check.Documents.SingleAsync(d => d.PersonalOfUserId != null);

        // The notebook went where a notebook may live — under the mailbox — rather than to My Documents, where
        // it would have been refused a second time for the same reason.
        var notebook = await check.Documents.SingleAsync(d => d.Name == PersonalRepositoryProvisioner.NotebookFolderName);
        var notebookParent = await check.Documents.SingleAsync(d => d.Id == notebook.ParentId);
        Assert.Equal(PersonalFolders.MyMailbox, notebookParent.Name);
        Assert.Equal(personal.Id, notebookParent.ParentId);

        // The loose item went to My Documents.
        var loose = await check.Documents.SingleAsync(d => d.Name == "Loose report");
        var looseParent = await check.Documents.SingleAsync(d => d.Id == loose.ParentId);
        Assert.Equal(PersonalFolders.MyDocuments, looseParent.Name);

        // …and so did the plain folder, since #634 closed the level to those as well. This assertion was the
        // reverse of itself one commit ago — it pinned that a folder was LEFT where the archive put it — and
        // flipping it is the two changes proving they are connected: the fallback reads the admitted set rather
        // than a list of its own, so tightening that set extended the fallback with no change here.
        var folder = await check.Documents.SingleAsync(d => d.Name == "Tax 2026");
        var folderParent = await check.Documents.SingleAsync(d => d.Id == folder.ParentId);
        Assert.Equal(PersonalFolders.MyDocuments, folderParent.Name);
    }

    [Fact]
    public async Task Importing_it_twice_does_not_produce_a_second_notebook()
    {
        // The mailbox holds at most one notebook, so a re-import has to find the first one rather than add a
        // rival — the same reason the fallback MAPS onto the existing node instead of re-parenting a copy.
        var (connection, accessor, _, email) = await TenantAsync();
        using var _c = connection;

        var storage = new InMemoryObjectStorage();
        var archiveOwnerId = Guid.NewGuid();

        for (var run = 0; run < 2; run++)
        {
            using var db = Ctx(connection, accessor);
            await Importer(db, storage, accessor).ImportAsync(
                ArchiveWithLegacyPersonalSpace(archiveOwnerId, email), targetFolderId: null,
                updateExisting: false, includePermissions: false, merge: false,
                LeafMergeMode.Rename, CancellationToken.None);
        }

        using var check = Ctx(connection, accessor);
        Assert.Equal(1, await check.Documents.CountAsync(d => d.Name == PersonalRepositoryProvisioner.NotebookFolderName));
    }
}
