using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The DocumentReference field type at the persistence layer: a field whose value names another document
// ("which flight was this lesson flown on"), validated in SaveChanges like every other field invariant.
//
// The validation is deliberately in TWO places and this file pins both halves: SHAPE in the pure
// FieldValueValidation ("is this an id at all"), EXISTENCE here in the DbContext, because it needs a query.
// Existence leans entirely on the two query filters — the tenant one is what stops a value naming another
// tenant's document, the soft-delete one what stops it naming something in the recycle bin — so a test that
// only checked a random GUID would pass against an implementation with no filters at all.
public class DocumentReferenceFieldTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<(Guid TenantId, Guid MaskVersionId, Guid LessonId, Guid FlightId, Guid UserId)> SeedAsync(
        SqliteConnection connection, string tenantName = "School")
    {
        var tenantId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var lessonId = Guid.NewGuid();
        var flightId = Guid.NewGuid();

        using var context = Ctx(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = tenantName, CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        context.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Lesson record", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = lessonId, TenantId = tenantId, Name = "Lesson 4", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = flightId, TenantId = tenantId, Name = "HB-PHG 2026-09-09", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        return (tenantId, maskVersionId, lessonId, flightId, userId);
    }

    private static async Task<Guid> AddFieldAsync(SqliteConnection connection, Guid tenantId, Guid maskVersionId, bool isList = false)
    {
        var fieldId = Guid.NewGuid();
        using var context = Ctx(connection);
        context.FieldDefinitions.Add(new FieldDefinition
        {
            Id = fieldId,
            TenantId = tenantId,
            MaskVersionId = maskVersionId,
            Name = "Flight",
            DataType = FieldDataType.DocumentReference,
            IsList = isList,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        return fieldId;
    }

    private static FieldValue Value(Guid tenantId, Guid documentId, Guid fieldId, string value, int ordinal = 0) =>
        new() { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = value, Ordinal = ordinal };

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var setup = Ctx(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task A_value_naming_a_real_document_is_stored()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, flightId, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId);

        using (var context = Ctx(connection, tenantId))
        {
            context.FieldValues.Add(Value(tenantId, lessonId, fieldId, flightId.ToString()));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, tenantId))
        {
            Assert.Equal(flightId.ToString(), await context.FieldValues.Where(v => v.DocumentId == lessonId).Select(v => v.Value).SingleAsync());
        }
    }

    [Fact]
    public async Task A_value_that_is_not_an_id_is_refused()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, _, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId);

        using var context = Ctx(connection, tenantId);
        context.FieldValues.Add(Value(tenantId, lessonId, fieldId, "the Tuesday flight"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("not a document id", error.Message);
    }

    [Fact]
    public async Task A_value_naming_no_document_is_refused()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, _, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId);

        using var context = Ctx(connection, tenantId);
        context.FieldValues.Add(Value(tenantId, lessonId, fieldId, Guid.NewGuid().ToString()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("does not name a document", error.Message);
    }

    // The tenant filter is what makes existence mean "in THIS tenant". Without it the check would happily
    // accept another tenant's document — a cross-tenant pointer that would then resolve to nothing on read,
    // which is the quietest possible way to leak that a document exists elsewhere.
    [Fact]
    public async Task A_value_naming_another_tenants_document_is_refused()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, _, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId);
        var (_, _, _, otherTenantsFlightId, _) = await SeedAsync(connection, "Another school");

        using var context = Ctx(connection, tenantId);
        context.FieldValues.Add(Value(tenantId, lessonId, fieldId, otherTenantsFlightId.ToString()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("does not name a document", error.Message);
    }

    // A document in the recycle bin is not a thing to point at: it is on its way out, and a value naming it
    // would be dangling the moment it is purged. The soft-delete filter gives this for free — which is worth
    // pinning precisely BECAUSE it is free: an implementation that bypassed the filter would look identical.
    [Fact]
    public async Task A_value_naming_a_soft_deleted_document_is_refused()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, flightId, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId);

        using (var context = Ctx(connection, tenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == flightId)).DeletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        using var write = Ctx(connection, tenantId);
        write.FieldValues.Add(Value(tenantId, lessonId, fieldId, flightId.ToString()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
        Assert.Contains("does not name a document", error.Message);
    }

    // Two values naming the SAME document in one save. Ordinary — a list repeating a target, or two fields
    // agreeing — and it broke the first draft of the existence check, which collected the ids into a
    // dictionary with Add and threw a duplicate-key error, turning a normal write into an invariant failure.
    [Fact]
    public async Task The_same_target_may_appear_twice_in_one_save()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, flightId, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId, isList: true);

        using (var context = Ctx(connection, tenantId))
        {
            context.FieldValues.Add(Value(tenantId, lessonId, fieldId, flightId.ToString(), ordinal: 0));
            context.FieldValues.Add(Value(tenantId, lessonId, fieldId, flightId.ToString(), ordinal: 1));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, tenantId))
        {
            Assert.Equal(2, await context.FieldValues.CountAsync(v => v.DocumentId == lessonId));
        }
    }

    // The asymmetry, stated as a test: existence is enforced when the value is WRITTEN, and a target disposed
    // of afterwards leaves the value alone. Refusing the delete instead would let a pointer veto a retention
    // act; the read side renders such a value as unavailable.
    [Fact]
    public async Task Deleting_the_target_afterwards_leaves_the_value_in_place()
    {
        using var connection = await OpenAsync();
        var (tenantId, maskVersionId, lessonId, flightId, _) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId);

        using (var context = Ctx(connection, tenantId))
        {
            context.FieldValues.Add(Value(tenantId, lessonId, fieldId, flightId.ToString()));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, tenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == flightId)).DeletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, tenantId))
        {
            Assert.Equal(flightId.ToString(), await context.FieldValues.Where(v => v.DocumentId == lessonId).Select(v => v.Value).SingleAsync());
        }
    }
}
