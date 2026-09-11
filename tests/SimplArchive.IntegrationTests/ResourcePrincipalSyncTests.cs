using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The mask declares WHICH of its fields names the person its documents represent (ABI 0.13, ADR 0779), and
// the core maintains ResourcePrincipal from it — at SaveChanges, the one door every write path uses.
//
// The declarative shape was chosen over a facade call the module makes because the imperative version fails
// SILENTLY: forget to call it and the person is simply not a claimant, so their calendar is empty, nothing
// errors, and nobody finds out for weeks. These tests are therefore mostly about the cases a forgotten call
// would have got wrong — an edit, and a re-point.
public class ResourcePrincipalSyncTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Fixture(Guid TenantId, Guid DossierId, Guid FieldId, Guid AnnaId, Guid TomId);

    // A "Pilot dossier"-shaped mask: one field, declared as the one naming its person.
    private static async Task<Fixture> SeedAsync(SqliteConnection connection, string? declaredField = "Email")
    {
        var tenantId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var dossierId = Guid.NewGuid();
        var annaId = Guid.NewGuid();
        var tomId = Guid.NewGuid();

        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "School", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = annaId, TenantId = tenantId, Email = "anna@school.test", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = tomId, TenantId = tenantId, Email = "tom@school.test", DisplayName = "Tom", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask
        {
            Id = maskId,
            TenantId = tenantId,
            IsFolderMask = true,
            IsBookable = true,
            RepresentsPrincipalField = declaredField,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.MaskVersions.Add(new MaskVersion { Id = versionId, TenantId = tenantId, MaskId = maskId, Name = "Pilot dossier", CreatedAt = DateTimeOffset.UtcNow });
        seed.FieldDefinitions.Add(new FieldDefinition
        {
            Id = fieldId,
            TenantId = tenantId,
            MaskVersionId = versionId,
            Name = "Email",
            DataType = FieldDataType.EmailAddress,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.Documents.Add(new Document
        {
            Id = dossierId,
            TenantId = tenantId,
            Name = "A. Muster",
            MaskVersionId = versionId,
            CreatedByUserId = annaId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await seed.SaveChangesAsync();

        return new Fixture(tenantId, dossierId, fieldId, annaId, tomId);
    }

    private static async Task<(SqliteConnection Connection, Fixture Fixture)> StartAsync(string? declaredField = "Email")
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        return (connection, await SeedAsync(connection, declaredField));
    }

    private static async Task WriteEmailAsync(SqliteConnection connection, Fixture f, string address)
    {
        using var db = CreateContext(connection, f.TenantId);
        var existing = await db.FieldValues.FirstOrDefaultAsync(v => v.DocumentId == f.DossierId && v.FieldDefinitionId == f.FieldId);
        if (existing is null)
        {
            db.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = f.TenantId,
                DocumentId = f.DossierId,
                FieldDefinitionId = f.FieldId,
                Value = address,
            });
        }
        else
        {
            existing.Value = address;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_mapping_is_written_when_the_MASK_arrives_after_the_field()
    {
        // THE ORDER EVERY REAL CALLER USES, and the one no other test here exercised: a document is created
        // bare, its index data is written, and the mask is assigned LAST. The core mandates exactly this —
        // required-field validation fires when the mask arrives (ADR 0176), so a mask cannot be assigned to a
        // document whose required field is still empty.
        //
        // Before the fix this produced NO mapping on either save: the field write found a document wearing no
        // mask, so nothing declared anything; the mask assignment wrote no field value, so the sync returned
        // at its first guard. ABI 0.13's feature was inert from the day it shipped, and invisibly so — a
        // document representing nobody is indistinguishable from one nobody has claimed.
        //
        // Found by driving the real demo stack. Every test in this file passed throughout, because the
        // fixture seeds the document ALREADY wearing its mask.
        var (connection, f) = await StartAsync();
        using var _ = connection;

        var bareId = Guid.NewGuid();
        Guid versionId;
        using (var db = CreateContext(connection, f.TenantId))
        {
            versionId = await db.Documents.Where(d => d.Id == f.DossierId).Select(d => d.MaskVersionId!.Value).SingleAsync();
            db.Documents.Add(new Document
            {
                Id = bareId,
                TenantId = f.TenantId,
                Name = "Filed bare",
                CreatedByUserId = f.AnnaId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, f.TenantId))
        {
            db.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = f.TenantId,
                DocumentId = bareId,
                FieldDefinitionId = f.FieldId,
                Value = "anna@school.test",
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, f.TenantId))
        {
            var bare = await db.Documents.SingleAsync(d => d.Id == bareId);
            bare.MaskVersionId = versionId;
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, f.TenantId))
        {
            var mapping = await db.ResourcePrincipals.SingleOrDefaultAsync(p => p.ResourceDocumentId == bareId);
            Assert.NotNull(mapping);
            Assert.Equal(f.AnnaId, mapping.UserId);
        }
    }

    [Fact]
    public async Task Writing_the_declared_field_maps_the_document_to_that_person()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        await WriteEmailAsync(connection, f, "anna@school.test");

        using var db = CreateContext(connection, f.TenantId);
        var principal = await db.ResourcePrincipals.SingleAsync();
        Assert.Equal(f.DossierId, principal.ResourceDocumentId);
        Assert.Equal(f.AnnaId, principal.UserId);
    }

    // The case an imperative "declare it once when you create the dossier" call gets wrong: somebody fixes a
    // typo in the address months later and the mapping keeps pointing at the old person.
    [Fact]
    public async Task Editing_the_declared_field_re_points_the_mapping()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        await WriteEmailAsync(connection, f, "anna@school.test");
        await WriteEmailAsync(connection, f, "tom@school.test");

        using var db = CreateContext(connection, f.TenantId);
        Assert.Equal(f.TomId, (await db.ResourcePrincipals.SingleAsync()).UserId);
    }

    // Matching is case-insensitive because the lookup goes through NormalizedEmail — the rule the User setter
    // maintains and the unique index is built on.
    [Fact]
    public async Task The_address_matches_regardless_of_case()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        await WriteEmailAsync(connection, f, "Anna@School.TEST");

        using var db = CreateContext(connection, f.TenantId);
        Assert.Equal(f.AnnaId, (await db.ResourcePrincipals.SingleAsync()).UserId);
    }

    // A dossier filed before its pilot has an account is an ordinary, correct state — refusing it would make
    // the module's provisioning order a constraint the core imposes.
    [Fact]
    public async Task An_address_with_no_user_represents_nobody_rather_than_failing()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        await WriteEmailAsync(connection, f, "nobody@school.test");

        using var db = CreateContext(connection, f.TenantId);
        Assert.Empty(await db.ResourcePrincipals.ToListAsync());
    }

    // ...and an existing mapping is REMOVED rather than left standing, which is the half worth testing: a
    // stale pointer would make somebody else's bookings look like the previous person's, which is worse than
    // no mapping at all.
    [Fact]
    public async Task Clearing_the_address_removes_the_mapping_rather_than_leaving_it_stale()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        await WriteEmailAsync(connection, f, "anna@school.test");
        await WriteEmailAsync(connection, f, "gone@school.test");

        using var db = CreateContext(connection, f.TenantId);
        Assert.Empty(await db.ResourcePrincipals.ToListAsync());
    }

    // A mask that declares nothing represents nobody — every mask in the archive except the few that opt in.
    [Fact]
    public async Task A_mask_that_declares_no_field_maps_nothing()
    {
        var (connection, f) = await StartAsync(declaredField: null);
        using var _ = connection;

        await WriteEmailAsync(connection, f, "anna@school.test");

        using var db = CreateContext(connection, f.TenantId);
        Assert.Empty(await db.ResourcePrincipals.ToListAsync());
    }
}
