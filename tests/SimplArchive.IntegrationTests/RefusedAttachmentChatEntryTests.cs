using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The pairing constraint after ADR 0718 widened it.
//
// `CK_ChatMessages_KindVersionPairing` deliberately "ruled out the renumbered-away fourth kind by leaving no
// value for it" (ADR 0545), which is exactly why a refused-attachment entry needed a migration rather than an
// enum edit. This asserts the widening went where it was meant to and nowhere else: the new kind names no
// version, and the two version-bearing kinds still cannot exist without one.
public class RefusedAttachmentChatEntryTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor accessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor);

    private static async Task<(SimplArchiveDbContext Db, Guid TenantId, Guid DocumentId, Guid VersionId, Guid UserId)> SeedAsync(
        SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var accessor = new CurrentTenantAccessor { TenantId = tenantId };
        var at = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
        var (documentId, versionId, userId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var db = CreateContext(connection, accessor);
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", CreatedAt = at });
        db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "post@acme.test", DisplayName = "Poster", CreatedAt = at });
        db.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Message", CreatedAt = at, CreatedByUserId = userId });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Confirmed,
            ObjectKey = "tenants/x/2026/y/content.eml",
            CreatedAt = at,
            CreatedByUserId = userId,
            VersionNumber = 1,
            Sha256Hash = new string('a', 64),
        });
        await db.SaveChangesAsync();

        return (db, tenantId, documentId, versionId, userId);
    }

    private static ChatMessage Entry(Guid tenantId, Guid documentId, Guid userId, ChatMessageKind kind, Guid? versionId, string body) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        DocumentId = documentId,
        Kind = kind,
        DocumentVersionId = versionId,
        Body = body,
        CreatedByUserId = userId,
        CreatedAt = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task A_refused_attachment_entry_names_no_version_and_carries_the_file_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (db, tenantId, documentId, _, userId) = await SeedAsync(connection);
        using var _1 = db;

        db.ChatMessages.Add(Entry(tenantId, documentId, userId, ChatMessageKind.AttachmentRefused, null, "invoice.exe"));
        await db.SaveChangesAsync();

        // The file name is in the BODY, deliberately: it is the one datum a client cannot compose, while the
        // sentence around it still lives in the clients' resources like every other system entry.
        var stored = await db.ChatMessages.SingleAsync(m => m.Kind == ChatMessageKind.AttachmentRefused);
        Assert.Equal("invoice.exe", stored.Body);
        Assert.Null(stored.DocumentVersionId);
    }

    [Fact]
    public async Task The_widening_did_not_loosen_the_two_kinds_that_must_name_a_version()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (db, tenantId, documentId, versionId, userId) = await SeedAsync(connection);
        using var _1 = db;

        // A filed entry with no version could not render its sentence, which is what the pairing exists for.
        db.ChatMessages.Add(Entry(tenantId, documentId, userId, ChatMessageKind.VersionFiled, null, string.Empty));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // …and the new kind may not name one either. It is about something that never became a version, so an
        // entry pointing at one would be describing a different fact.
        db.ChatMessages.Add(Entry(tenantId, documentId, userId, ChatMessageKind.AttachmentRefused, versionId, "invoice.exe"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
