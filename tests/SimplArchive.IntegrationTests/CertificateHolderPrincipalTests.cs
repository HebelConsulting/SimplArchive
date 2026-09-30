using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A certificate document says WHOSE it is (ABI 1.2, ADR 0851).
//
// The three decisions this reconciles could not all hold before it: Holder is an e-mail because a person reads
// it, the capability is keyed on a user id because an address changes, and a module read model must be
// rebuildable from documents alone. The core already maintained the mapping for bookings; declaring
// Certificate → Holder is what lets a module read it.
//
// THE NULL CASE IS TESTED TOO, because null has two causes and only one is correct here: nobody to resolve, or
// nothing resolving. ABI 0.13's own feature was inert from the day it shipped and nobody noticed, precisely
// because a document representing nobody looks exactly like a document nobody has claimed.
public class CertificateHolderPrincipalTests
{
    private static SimplArchiveDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task The_Holder_field_resolves_to_the_user_it_names()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId, email) = await SeedAsync(connection);

        var documentId = await FileCertificateAsync(connection, tenantId, userId, holder: email);

        using var db = Context(connection, tenantId);
        var principal = await db.ResourcePrincipals.FirstOrDefaultAsync(p => p.ResourceDocumentId == documentId);

        Assert.NotNull(principal);
        Assert.Equal(userId, principal.UserId);
    }

    [Fact]
    public async Task A_holder_who_is_not_a_user_of_the_tenant_resolves_to_NOBODY()
    {
        // A CA's own certificate, filed for reference — core ADR 0844 keeps that legitimate, so it must not
        // be an error. It is simply addressed to nobody, which is exactly what the module should conclude.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId, _) = await SeedAsync(connection);

        var documentId = await FileCertificateAsync(connection, tenantId, userId, holder: "a-ca@elsewhere.test");

        using var db = Context(connection, tenantId);
        Assert.Null(await db.ResourcePrincipals.FirstOrDefaultAsync(p => p.ResourceDocumentId == documentId));
    }

    [Fact]
    public async Task The_declaration_is_HEALED_onto_a_mask_that_predates_it()
    {
        // ADR 0757's reason, applied: activation-only seeding stranded upgrades, so masks heal on startup. A
        // tenant whose Certificate mask predates the declaration would resolve no holder at all — and that
        // failure is invisible, so the heal is what makes the feature true for existing tenants rather than
        // only for new ones.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, _, _) = await SeedAsync(connection);

        using (var regress = Context(connection, tenantId))
        {
            var stale = await regress.Masks.FirstAsync(m => m.Id == WellKnownMaskIds.Certificate);
            stale.RepresentsPrincipalField = null;
            await regress.SaveChangesAsync();
        }

        using (var heal = Context(connection, tenantId))
        {
            await new WellKnownMaskSeeder(heal, NullLogger<WellKnownMaskSeeder>.Instance)
                .EnsureWellKnownMasksAsync(tenantId);
        }

        using var db = Context(connection, tenantId);
        var mask = await db.Masks.FirstAsync(m => m.Id == WellKnownMaskIds.Certificate);
        Assert.Equal("Holder", mask.RepresentsPrincipalField);
    }

    [Fact]
    public void Only_the_Certificate_mask_declares_a_principal_field()
    {
        // The list is one entry, and a second should be argued for: declaring a field makes the core maintain
        // a mapping for every document wearing that mask, on every save.
        Assert.Equal([WellKnownMaskIds.Certificate], WellKnownMaskIds.PrincipalFields.Keys);
        Assert.Equal("Holder", WellKnownMaskIds.PrincipalFields[WellKnownMaskIds.Certificate]);
    }

    // --- harness ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_document_created_in_ONE_save_with_its_mask_and_fields_still_resolves_its_holder()
    {
        // THE ORDER A MODULE USES, and the one this file did not cover (#1496). `CreateDocumentAsync` Adds
        // the document WITH its MaskVersionId, Adds its field values, and saves ONCE — so neither branch of
        // the sync saw it: `IsModified` is false for an Added entity, and the fallback query cannot find a
        // row that this very save is inserting.
        //
        // The cost of that gap was not theoretical. An encryption certificate enrolled through the module's
        // own surface answered 201 with usable: true, represented nobody, never reached the module's read
        // model, and so the module answered "this reader holds none" — which, because a module that answers
        // is the ONLY source (ADR 0842), REMOVED the reader's access that the service registry had provided.
        // Every layer behaved as designed.
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId, email) = await SeedAsync(connection);

        var documentId = await FileCertificateInOneSaveAsync(connection, tenantId, userId, email);

        using var read = Context(connection, tenantId);
        var principal = await read.ResourcePrincipals
            .FirstOrDefaultAsync(p => p.ResourceDocumentId == documentId);

        Assert.NotNull(principal);
        Assert.Equal(userId, principal!.UserId);
    }

    /// <summary>
    /// Files a certificate the way <c>IModuleArchiveFacade.CreateDocumentAsync</c> does: one save, mask and
    /// fields together.
    /// </summary>
    /// <remarks>
    /// Deliberately a SECOND helper rather than a parameter on the first. The two orders are different facts
    /// about the core — one is what the metadata endpoint does, the other is what every module does — and a
    /// single helper with a flag would let a future edit "simplify" one of them away, which is how this gap
    /// stayed open while a test for the feature passed.
    /// </remarks>
    private static async Task<Guid> FileCertificateInOneSaveAsync(
        SqliteConnection connection, Guid tenantId, Guid userId, string holder)
    {
        using var db = Context(connection, tenantId);

        var maskVersionId = await db.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Certificate && v.IsCurrent)
            .Select(v => v.Id).FirstAsync();
        var holderFieldId = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId && f.Name == "Holder")
            .Select(f => f.Id).FirstAsync();

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"cert-{Guid.NewGuid():N}",
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = document.Id,
            FieldDefinitionId = holderFieldId,
            Value = holder,
        });

        await db.SaveChangesAsync();   // ONE save — the whole point
        return document.Id;
    }

    private static async Task<(Guid TenantId, Guid UserId, string Email)> SeedAsync(SqliteConnection connection)
    {
        using (var setup = Context(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var email = $"holder-{Guid.NewGuid():N}@example.com";

        using var seed = Context(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = email, DisplayName = "H", CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        using var masks = Context(connection, tenantId);
        await new WellKnownMaskSeeder(masks, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        return (tenantId, userId, email);
    }

    private static async Task<Guid> FileCertificateAsync(
        SqliteConnection connection, Guid tenantId, Guid userId, string holder)
    {
        using var db = Context(connection, tenantId);

        // The order the core MANDATES: create bare, write the index value, then assign the mask — because
        // required-field validation fires when the mask arrives (ADR 0176). It is also the order that made
        // ABI 0.13's feature inert, since neither the field write nor the mask assignment alone triggered the
        // sync. Filing it the real way is the only way this test can tell a working mapping from a dead one.
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"cert-{Guid.NewGuid():N}",
            MaskVersionId = Guid.Empty,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var maskVersionId = await db.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Certificate && v.IsCurrent)
            .Select(v => v.Id).FirstAsync();
        var holderFieldId = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId && f.Name == "Holder")
            .Select(f => f.Id).FirstAsync();

        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = document.Id,
            FieldDefinitionId = holderFieldId,
            Value = holder,
        });
        await db.SaveChangesAsync();

        document.MaskVersionId = maskVersionId;
        await db.SaveChangesAsync();

        return document.Id;
    }
}
