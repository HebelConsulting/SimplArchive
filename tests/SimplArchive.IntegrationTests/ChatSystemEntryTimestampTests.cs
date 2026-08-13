using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A "filed a new document" feed entry (ADR 0545) is dated from the VERSION it records, not from the moment the
// row is written (issue #478).
//
// It used to take DateTimeOffset.UtcNow. For an interactive upload the two are the same instant, which is why
// it went unnoticed — but they are months apart wherever a version carries a date of its own: the demo seed
// files documents dated in the past, so the kiosk's chat feed showed "filed a new document" stamped TODAY
// beside a version from June, and the manual's chat screenshots changed on every capture run because the
// timestamp was literally the moment of capture.
//
// Tested HERE rather than end-to-end on purpose: over the API both instants are ~now, so an E2E assertion
// would pass against the bug it is meant to catch. A version created in the past is the only shape that can
// actually fail, and this is where one can be constructed.
public class ChatSystemEntryTimestampTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenantAccessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenantAccessor);

    [Fact]
    public async Task A_filed_entry_is_dated_from_its_version_not_from_now()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantId = Guid.NewGuid();
        var tenantAccessor = new CurrentTenantAccessor { TenantId = tenantId };
        var filedAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero); // months before "now"

        var documentId = Guid.NewGuid();
        var authorId = Guid.NewGuid(); // CK_Documents_ExactlyOneCreator wants exactly one of user/service-account
        var versionId = Guid.NewGuid();

        using (var setup = CreateContext(connection, tenantAccessor))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", CreatedAt = filedAt });
            setup.Users.Add(new User { Id = authorId, TenantId = tenantId, Email = "seed@acme.test", DisplayName = "Seeder", CreatedAt = filedAt });
            setup.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Seeded", CreatedAt = filedAt, CreatedByUserId = authorId });
            setup.DocumentVersions.Add(new DocumentVersion
            {
                Id = versionId,
                TenantId = tenantId,
                DocumentId = documentId,
                Status = DocumentVersionStatus.Confirmed,
                ObjectKey = "tenants/x/2026/y/content.txt",
                CreatedAt = filedAt,
                CreatedByUserId = authorId,
                // A Confirmed version must carry both (CK_DocumentVersions_Status_VersionNumber_Sha256Hash).
                VersionNumber = 1,
                Sha256Hash = new string('a', 64),
            });
            await setup.SaveChangesAsync();
        }

        using (var context = CreateContext(connection, tenantAccessor))
        {
            var version = await context.DocumentVersions.SingleAsync(v => v.Id == versionId);

            // The clock deliberately reads a DIFFERENT instant from the version's: if the recorder ever goes
            // back to stamping "now", this is what catches it.
            var recorder = new ChatSystemEntryRecorder(context, TimeProvider.System);
            await recorder.RecordVersionFiledAsync(version, CancellationToken.None);
        }

        using (var verify = CreateContext(connection, tenantAccessor))
        {
            var entry = await verify.ChatMessages.SingleAsync(m => m.DocumentId == documentId);

            Assert.Equal(ChatMessageKind.VersionFiled, entry.Kind);
            Assert.Equal(filedAt, entry.CreatedAt);
        }
    }
}
