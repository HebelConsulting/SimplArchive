using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Typed-folder containment written into the MODEL (#673): where each mask may live, what each folder admits,
// and the two one-directional flags. The invariant still reads the static tables, so nothing behaves
// differently yet — these assert the DATABASE now says the same thing, which is what the port will move onto.
//
// MaskContainmentEquivalenceTests proves the static rules and the four facts agree in the abstract. This proves
// the four facts actually REACH a tenant — including one that already existed, which is the half that fails
// quietly (#664).
public class MaskContainmentModelTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private SimplArchiveDbContext Ctx(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task<SqliteConnection> SeededTenantAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();
        using (var db = Ctx(connection))
        {
            db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await SeedAsync(connection);
        return connection;
    }

    private async Task SeedAsync(SqliteConnection connection)
    {
        using var db = Ctx(connection);
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance)
            .EnsureWellKnownMasksAsync(_tenantId);
    }

    private async Task<(HashSet<(Guid, Guid)> Parents, HashSet<(Guid, Guid)> Children)> ContainmentAsync(SqliteConnection connection)
    {
        using var db = Ctx(connection);
        var parents = await db.MaskAllowedParents.IgnoreQueryFilters(["TenantFilter"])
            .Where(p => p.TenantId == _tenantId).Select(p => new { p.MaskId, p.ParentMaskId }).ToListAsync();
        var children = await db.MaskAdmittedChildren.IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.TenantId == _tenantId).Select(c => new { c.FolderMaskId, c.ChildMaskId }).ToListAsync();

        return (parents.Select(p => (p.MaskId, p.ParentMaskId)).ToHashSet(),
                children.Select(c => (c.FolderMaskId, c.ChildMaskId)).ToHashSet());
    }

    private static HashSet<(Guid, Guid)> ExpectedParents() =>
        WellKnownMaskIds.AllowedParentMasks
            .SelectMany(pair => pair.Value.Select(parent => (pair.Key, parent)))
            .ToHashSet();

    private static HashSet<(Guid, Guid)> ExpectedChildren() =>
        WellKnownMaskIds.AdmittedChildMasks
            .SelectMany(pair => pair.Value.Select(child => (pair.Key, child)))
            .ToHashSet();

    [Fact]
    public async Task A_fresh_tenant_gets_every_containment_rule()
    {
        using var connection = await SeededTenantAsync();
        var (parents, children) = await ContainmentAsync(connection);

        // Compared as whole SETS rather than by spot-checking a few rows: the failure mode that matters is a
        // rule going MISSING, and a test that only asserts what it expects to find cannot see an absence.
        Assert.Equal(ExpectedParents(), parents);
        Assert.Equal(ExpectedChildren(), children);

        // Named individually too, because the set comparison above is derived from the same projections the
        // seeder reads — so it would still pass if a projection were empty on both sides. These are the rules a
        // human would name, restated by hand on purpose.
        Assert.Contains((WellKnownMaskIds.Contact, WellKnownMaskIds.Addressbook), parents);
        Assert.Contains((WellKnownMaskIds.Appointment, WellKnownMaskIds.Calendar), parents);
        Assert.Contains((WellKnownMaskIds.Note, WellKnownMaskIds.Notebook), parents);
        Assert.Contains((WellKnownMaskIds.Note, WellKnownMaskIds.NotebookSection), parents);
        Assert.Contains((WellKnownMaskIds.Notebook, WellKnownMaskIds.Mailbox), parents);

        // A Section inside a Section — the self-reference that stopped containment being a list of pairs.
        Assert.Contains((WellKnownMaskIds.NotebookSection, WellKnownMaskIds.NotebookSection), children);

        // A Mailbox also takes ordinary folders, and — the half that makes it safe — a plain Folder is still
        // welcome anywhere, because it has no allowed-parent row of its own.
        Assert.Contains((WellKnownMaskIds.Mailbox, WellKnownMaskIds.Folder), children);
        Assert.DoesNotContain(parents, p => p.Item1 == WellKnownMaskIds.Folder);
    }

    [Fact]
    public async Task The_two_one_directional_flags_land_on_the_right_masks()
    {
        using var connection = await SeededTenantAsync();
        using var db = Ctx(connection);

        var masks = await db.Masks.IgnoreQueryFilters(["TenantFilter"])
            .Where(m => m.TenantId == _tenantId)
            .ToDictionaryAsync(m => m.Id, m => (m.AdmitsOnlyDeclaredChildren, m.AdmitsNoSubfolders));

        Assert.True(masks[WellKnownMaskIds.Addressbook].AdmitsOnlyDeclaredChildren);
        Assert.True(masks[WellKnownMaskIds.Mailbox].AdmitsOnlyDeclaredChildren);

        // A plain Folder holds anything — the permissive default every mask had before containment was
        // modelled, and the one an exclusivity bug would silently take away.
        Assert.False(masks[WellKnownMaskIds.Folder].AdmitsOnlyDeclaredChildren);
        Assert.False(masks[WellKnownMaskIds.BasicEntry].AdmitsOnlyDeclaredChildren);

        // An IMAP Special folder holds documents only. Deliberately NOT expressed as "admits eMail": that would
        // also refuse an ordinary document, which the rule permits, and no list of masks can name the
        // open-ended set "anything that is not a folder".
        Assert.True(masks[WellKnownMaskIds.ImapSpecial].AdmitsNoSubfolders);
        Assert.False(masks[WellKnownMaskIds.ImapSpecial].AdmitsOnlyDeclaredChildren);
        Assert.Empty(await db.MaskAdmittedChildren.IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.TenantId == _tenantId && c.FolderMaskId == WellKnownMaskIds.ImapSpecial).ToListAsync());

        Assert.All(masks.Where(m => m.Key != WellKnownMaskIds.ImapSpecial),
            m => Assert.False(m.Value.AdmitsNoSubfolders, $"{m.Key} claimed to hold no subfolders."));
    }

    [Fact]
    public async Task A_tenant_seeded_before_the_model_existed_is_healed()
    {
        using var connection = await SeededTenantAsync();

        // Wind the tenant back to what an upgrade actually finds: the masks exist, both flags sit at their
        // default and not one containment row is present. That state reads as "no restrictions" — the
        // PERMISSIVE direction, so an unhealed tenant does not fail, it silently admits everything.
        using (var stale = Ctx(connection))
        {
            foreach (var mask in await stale.Masks.IgnoreQueryFilters(["TenantFilter"])
                         .Where(m => m.TenantId == _tenantId).ToListAsync())
            {
                mask.AdmitsOnlyDeclaredChildren = false;
                mask.AdmitsNoSubfolders = false;
            }

            stale.MaskAllowedParents.RemoveRange(await stale.MaskAllowedParents
                .IgnoreQueryFilters(["TenantFilter"]).Where(p => p.TenantId == _tenantId).ToListAsync());
            stale.MaskAdmittedChildren.RemoveRange(await stale.MaskAdmittedChildren
                .IgnoreQueryFilters(["TenantFilter"]).Where(c => c.TenantId == _tenantId).ToListAsync());
            await stale.SaveChangesAsync();
        }

        await SeedAsync(connection);

        var (parents, children) = await ContainmentAsync(connection);
        Assert.Equal(ExpectedParents(), parents);
        Assert.Equal(ExpectedChildren(), children);

        using var db = Ctx(connection);
        Assert.True((await db.Masks.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(m => m.TenantId == _tenantId && m.Id == WellKnownMaskIds.Addressbook)).AdmitsOnlyDeclaredChildren);
        Assert.True((await db.Masks.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(m => m.TenantId == _tenantId && m.Id == WellKnownMaskIds.ImapSpecial)).AdmitsNoSubfolders);
    }

    [Fact]
    public async Task A_rule_that_no_longer_applies_is_removed()
    {
        using var connection = await SeededTenantAsync();

        // A leftover containment row is PERMISSIVE — it goes on admitting something the rules stopped
        // admitting — so a seed that only ever grows would leave the archive quietly wrong with nothing
        // reporting it. This is the direction that has to be reconciled rather than appended to.
        using (var drifted = Ctx(connection))
        {
            drifted.MaskAdmittedChildren.Add(new MaskAdmittedChild
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                FolderMaskId = WellKnownMaskIds.Addressbook,
                ChildMaskId = WellKnownMaskIds.EMail,
            });
            await drifted.SaveChangesAsync();
        }

        await SeedAsync(connection);

        var (_, children) = await ContainmentAsync(connection);
        Assert.DoesNotContain((WellKnownMaskIds.Addressbook, WellKnownMaskIds.EMail), children);
        Assert.Equal(ExpectedChildren(), children);
    }

    [Fact]
    public async Task A_tenants_own_declaration_is_not_the_seeds_to_delete()
    {
        using var connection = await SeededTenantAsync();

        // The reconcile above owns rows between WELL-KNOWN masks. A tenant-authored mask declared into an
        // Addressbook is somebody's decision, and "not in my table" must not read as "wrong" — the same
        // boundary the field-heal keeps when it adds missing fields without pruning unknown ones.
        var ownMaskId = Guid.NewGuid();
        using (var authored = Ctx(connection))
        {
            authored.Masks.Add(new Mask { Id = ownMaskId, TenantId = _tenantId, CreatedAt = DateTimeOffset.UtcNow });
            authored.MaskVersions.Add(new MaskVersion
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                MaskId = ownMaskId,
                Name = "House Rule",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await authored.SaveChangesAsync();

            authored.MaskAdmittedChildren.Add(new MaskAdmittedChild
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                FolderMaskId = WellKnownMaskIds.Addressbook,
                ChildMaskId = ownMaskId,
            });
            authored.MaskAllowedParents.Add(new MaskAllowedParent
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                MaskId = ownMaskId,
                ParentMaskId = WellKnownMaskIds.Addressbook,
            });
            await authored.SaveChangesAsync();
        }

        await SeedAsync(connection);

        var (parents, children) = await ContainmentAsync(connection);
        Assert.Contains((WellKnownMaskIds.Addressbook, ownMaskId), children);
        Assert.Contains((ownMaskId, WellKnownMaskIds.Addressbook), parents);
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_a_rule()
    {
        using var connection = await SeededTenantAsync();
        await SeedAsync(connection);

        // The seeder runs on EVERY startup for every tenant, so a seed that appended would violate the unique
        // index on the second boot rather than the hundredth.
        var (parents, children) = await ContainmentAsync(connection);
        Assert.Equal(ExpectedParents(), parents);
        Assert.Equal(ExpectedChildren(), children);
    }

    [Fact]
    public async Task The_same_rule_cannot_be_declared_twice()
    {
        using var connection = await SeededTenantAsync();
        using var db = Ctx(connection);

        db.MaskAllowedParents.Add(new MaskAllowedParent
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            MaskId = WellKnownMaskIds.Contact,
            ParentMaskId = WellKnownMaskIds.Addressbook,
        });

        // A duplicate says nothing the first row did not, and would make the invariant's message read
        // "belongs in Addressbook or Addressbook" — a bug in the data presenting as a bug in the wording.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
