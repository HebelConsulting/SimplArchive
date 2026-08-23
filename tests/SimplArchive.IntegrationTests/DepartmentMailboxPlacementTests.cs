using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Where a department mailbox may live (#703 PR 4): only a folder wearing the plain Folder mask holds one, at
// most one per folder — so typed containment stays intact (no mailbox-in-mailbox, no mailbox-in-calendar),
// and a repository ROOT is excluded because it wears the Repository mask (ADR 0627). The personal-space
// admission (#634) is the standing exception and keeps its own tests (MailboxCardinalityTests).
//
// The load-bearing rows are the refusals: placement went from "provisioning only" to constrained-but-open,
// and the permissive direction — a mailbox landing where nothing routes to it, or two claiming one folder —
// is the silent one.
public class DepartmentMailboxPlacementTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<(SqliteConnection Connection, Guid RepoId, Guid FolderId, Guid MailboxVersion, Guid CalendarFolderId)> TreeAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new SimplArchive.Domain.Users.User
            {
                Id = _ownerId,
                TenantId = _tenantId,
                Email = "o@t.test",
                DisplayName = "O",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
        }

        async Task<Guid> Version(Guid maskId)
        {
            using var db = Ctx(connection);
            return await db.MaskVersions.IgnoreQueryFilters()
                .Where(v => v.TenantId == _tenantId && v.MaskId == maskId && v.IsCurrent)
                .Select(v => v.Id).SingleAsync();
        }

        var repoVersion = await Version(WellKnownMaskIds.Repository);
        var folderVersion = await Version(WellKnownMaskIds.Folder);
        var calendarVersion = await Version(WellKnownMaskIds.Calendar);
        var mailboxVersion = await Version(WellKnownMaskIds.Mailbox);

        var repo = Doc("Repo", null, repoVersion);
        var folder = Doc("Sales", repo.Id, folderVersion);
        var calendar = Doc("Calendar", folder.Id, calendarVersion);
        using (var db = Ctx(connection))
        {
            db.Documents.AddRange(repo, folder, calendar);
            await db.SaveChangesAsync();
        }

        return (connection, repo.Id, folder.Id, mailboxVersion, calendar.Id);
    }

    private Document Doc(string name, Guid? parentId, Guid maskVersionId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        ParentId = parentId,
        Name = name,
        MaskVersionId = maskVersionId,
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedByUserId = _ownerId,
    };

    [Fact]
    public async Task A_plain_folder_admits_one_mailbox_and_refuses_a_second()
    {
        var (connection, _, folderId, mailboxVersion, _) = await TreeAsync();
        using var _c = connection;

        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Mailbox", folderId, mailboxVersion));
            await db.SaveChangesAsync(); // admitted: the whole point of PR 4
        }

        // ONE per folder — the same capacity shape as the personal space's rule, extended not forked. A
        // second is not a placement error, it is one too many: delivery fanning into two mailboxes in one
        // folder is a decision the CLAIMS express, never the tree.
        using (var db = Ctx(connection))
        {
            db.Documents.Add(Doc("Mailbox 2", folderId, mailboxVersion));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_repository_root_refuses_a_mailbox()
    {
        var (connection, repoId, _, mailboxVersion, _) = await TreeAsync();
        using var _c = connection;

        // The root wears Repository (ADR 0627), which the constrained placement deliberately omits: a
        // department mailbox lives in a NAMED plain folder (`Sales/Mailbox`), which is also where it reads
        // naturally — decided 2026-08-22 on the issue.
        using var db = Ctx(connection);
        db.Documents.Add(Doc("Mailbox", repoId, mailboxVersion));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_typed_container_refuses_a_mailbox()
    {
        var (connection, _, _, mailboxVersion, calendarId) = await TreeAsync();
        using var _c = connection;

        // Typed containers admit only what they declare, and none declares a Mailbox — which is also what
        // keeps mailbox-in-mailbox impossible by construction rather than by an extra rule.
        using var db = Ctx(connection);
        db.Documents.Add(Doc("Mailbox", calendarId, mailboxVersion));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
