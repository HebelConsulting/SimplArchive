using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The other half of the upgrade-path defect that PersonalNotesMaskHealTests covers. That one is about a well-known
// mask that does not EXIST yet in an older tenant; this one is about a well-known mask that exists but is MISSING
// FIELDS, because its field set was fixed when the tenant was seeded and every field added afterwards reached only
// tenants provisioned later. ADR 0587 added three fields to the eMail mask and they never arrived anywhere they
// were needed: the mask existed, so the seeder returned early and never looked at its fields.
public class WellKnownMaskFieldHealTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    private static WellKnownMaskSeeder Seeder(SimplArchiveDbContext db) =>
        new(db, NullLogger<WellKnownMaskSeeder>.Instance);

    private async Task<List<string>> FieldNamesAsync(SimplArchiveDbContext db, Guid maskId) =>
        await db.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == _tenantId && v.MaskId == maskId && v.IsCurrent)
            .Join(db.FieldDefinitions.IgnoreQueryFilters(), v => v.Id, f => f.MaskVersionId, (_, f) => f.Name)
            .ToListAsync();

    [Fact]
    public async Task A_field_added_to_a_well_known_mask_reaches_a_tenant_that_predates_it()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Older", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // The tenant is seeded, then its eMail mask is cut back to the six fields it had before ADR 0587 — this is
        // what every tenant provisioned before that ADR actually looks like today.
        using (var db = Ctx(connection, accessor))
        {
            await Seeder(db).EnsureWellKnownMasksAsync(_tenantId);
        }

        var addedByAdr0587 = new[] { "Conversation ID", "Mailbox path", "Reference" };
        using (var db = Ctx(connection, accessor))
        {
            var stale = await db.FieldDefinitions.IgnoreQueryFilters()
                .Where(f => addedByAdr0587.Contains(f.Name)).ToListAsync();
            db.FieldDefinitions.RemoveRange(stale);
            await db.SaveChangesAsync();
            Assert.Equal(6, (await FieldNamesAsync(db, WellKnownMaskIds.EMail)).Count);
        }

        // The upgrade arrives: the startup backfill runs for the existing tenant, as Program.cs does per tenant.
        using (var db = Ctx(connection, accessor))
        {
            await Seeder(db).EnsureWellKnownMasksAsync(_tenantId);
        }

        using (var db = Ctx(connection, accessor))
        {
            var names = await FieldNamesAsync(db, WellKnownMaskIds.EMail);
            Assert.Equal(9, names.Count);
            foreach (var field in addedByAdr0587)
            {
                Assert.Contains(field, names);
            }

            // Added as OPTIONAL — a required one would retroactively invalidate every document already on the mask.
            var added = await db.FieldDefinitions.IgnoreQueryFilters()
                .Where(f => addedByAdr0587.Contains(f.Name)).ToListAsync();
            Assert.All(added, f => Assert.False(f.IsRequired));

            // Healed onto the mask's CURRENT version, not a new one: no version bump, so nothing needs re-pointing
            // and every document already carrying the mask sees the new fields immediately.
            Assert.Equal(1, await db.MaskVersions.IgnoreQueryFilters()
                .CountAsync(v => v.TenantId == _tenantId && v.MaskId == WellKnownMaskIds.EMail));
        }
    }

    [Fact]
    public async Task The_heal_is_idempotent_and_does_not_duplicate_fields()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Older", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Three startups in a row — the seeder runs per tenant on every one of them.
        for (var i = 0; i < 3; i++)
        {
            using var db = Ctx(connection, accessor);
            await Seeder(db).EnsureWellKnownMasksAsync(_tenantId);
        }

        using (var db = Ctx(connection, accessor))
        {
            var names = await FieldNamesAsync(db, WellKnownMaskIds.EMail);
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Equal(9, names.Count);

            // Every well-known mask stays on exactly one version — the probe must never mint one. The count
            // tracks the well-known set (11 since #564 added the Contact/Calendar pairs and then the notebook
            // Section): asserting the two counts are EQUAL is the invariant, and the literal additionally
            // catches a mask appearing by accident, so both are kept.
            //
            // Note the Notebook itself is NOT a new mask here — "Note Folder" → "Notebook" is a rename on the
            // same id, which is what keeps the upgrade free of any document movement.
            //
            // 11 → 12 with Repository (ADR 0627, #596), which unlike the Notebook case IS a genuinely new mask:
            // a repository previously wore the plain Folder mask, so this one has its own id and existing
            // repositories are moved onto it by the seeder's backfill. 12 → 13 with Mailbox (ADR 0628),
            // 13 → 14 with IMAP Special (#596) — the mask that marks a mailbox's standing folders ephemeral.
            var maskCount = await db.Masks.IgnoreQueryFilters().CountAsync(m => m.TenantId == _tenantId);
            Assert.Equal(14, maskCount);
            Assert.Equal(maskCount, await db.MaskVersions.IgnoreQueryFilters().CountAsync(v => v.TenantId == _tenantId));
        }
    }

    [Fact]
    public async Task An_existing_mask_missing_a_REQUIRED_field_fails_loudly_rather_than_invalidating_documents()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Older", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await Seeder(db).EnsureWellKnownMasksAsync(_tenantId);
        }

        // Simulate a release that dropped a REQUIRED field into a well-known mask's spec: remove one the seeder
        // declares as required, so the next probe finds it missing.
        using (var db = Ctx(connection, accessor))
        {
            var subject = await db.FieldDefinitions.IgnoreQueryFilters().SingleAsync(f => f.Name == "Subject");
            db.FieldDefinitions.Remove(subject);
            await db.SaveChangesAsync();
        }

        // It refuses. Adding it would retroactively invalidate every document already on the mask (ADR 0176), and
        // skipping it quietly would leave the mask permanently wrong with nobody told.
        using (var db = Ctx(connection, accessor))
        {
            var thrown = await Assert.ThrowsAsync<RequiredFieldAddedToWellKnownMaskException>(
                () => Seeder(db).EnsureWellKnownMasksAsync(_tenantId));
            Assert.Contains("Subject", thrown.FieldNames);
            Assert.Equal(WellKnownMaskIds.EMail, thrown.MaskId);
        }
    }
}
