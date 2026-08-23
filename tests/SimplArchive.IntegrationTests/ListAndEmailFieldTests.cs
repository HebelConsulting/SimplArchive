using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Lists as an orthogonal property, and the EmailAddress type, at the persistence layer (#703).
//
// The point being pinned here is that ONE seam does both jobs. ValidateFormatAndRange already runs once per
// FieldValue row, so a list is n rows and every element is checked with no multiplicity logic in the
// validator at all — which is exactly why a bad element in the middle of an otherwise good list must fail.
public class ListAndEmailFieldTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<(Guid TenantId, Guid MaskVersionId, Guid DocumentId)> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using var context = Ctx(connection);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        context.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        context.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Mailbox", CreatedAt = DateTimeOffset.UtcNow });
        context.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Events", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        return (tenantId, maskVersionId, documentId);
    }

    private static async Task<Guid> AddFieldAsync(
        SqliteConnection connection, Guid tenantId, Guid maskVersionId, FieldDataType type, bool isList)
    {
        var fieldId = Guid.NewGuid();
        using var context = Ctx(connection);
        context.FieldDefinitions.Add(new FieldDefinition
        {
            Id = fieldId,
            TenantId = tenantId,
            MaskVersionId = maskVersionId,
            Name = "eMail Addresses",
            DataType = type,
            IsList = isList,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        return fieldId;
    }

    private static FieldValue Value(Guid tenantId, Guid documentId, Guid fieldId, string value, int ordinal = 0) =>
        new() { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = value, Ordinal = ordinal };

    [Fact]
    public async Task A_list_field_stores_every_element()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId, FieldDataType.EmailAddress, isList: true);

        using (var context = Ctx(connection, tenantId))
        {
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "events@demo.dev"));
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "veranstaltungen@demo.dev"));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, tenantId))
        {
            var stored = await context.FieldValues.Where(v => v.FieldDefinitionId == fieldId).Select(v => v.Value).ToListAsync();
            Assert.Equal(2, stored.Count);
        }
    }

    [Fact]
    public async Task A_lists_order_is_its_own_and_survives_a_round_trip()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId, FieldDataType.Text, isList: true);

        // Inserted in an order that does NOT match the ordinals, deliberately: an implementation that relied
        // on insertion order, or on the id, would pass a test whose two orders agreed.
        using (var context = Ctx(connection, tenantId))
        {
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "third", ordinal: 2));
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "first", ordinal: 0));
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "second", ordinal: 1));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, tenantId))
        {
            var ordered = await context.FieldValues
                .Where(v => v.FieldDefinitionId == fieldId)
                .OrderBy(v => v.Ordinal).ThenBy(v => v.Id)
                .Select(v => v.Value)
                .ToListAsync();

            Assert.Equal(["first", "second", "third"], ordered);
        }
    }

    [Fact]
    public async Task One_bad_element_fails_the_whole_list()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId, FieldDataType.EmailAddress, isList: true);

        using (var context = Ctx(connection, tenantId))
        {
            // Good, BAD, good — deliberately not first, because a validator that checked only the head of a
            // list would still pass a test that put the bad value there.
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "events@demo.dev"));
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "not-an-address"));
            context.FieldValues.Add(Value(tenantId, documentId, fieldId, "veranstaltungen@demo.dev"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }

        // And nothing was written: the save is one transaction, so the good elements do not survive a
        // rejection of their neighbour.
        using (var context = Ctx(connection, tenantId))
        {
            Assert.Empty(await context.FieldValues.Where(v => v.FieldDefinitionId == fieldId).ToListAsync());
        }
    }

    [Fact]
    public async Task An_email_address_field_refuses_a_malformed_value()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId, FieldDataType.EmailAddress, isList: false);

        using var context = Ctx(connection, tenantId);
        context.FieldValues.Add(Value(tenantId, documentId, fieldId, "events"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("e-mail address", error.Message);
    }

    [Fact]
    public async Task A_text_field_holding_the_same_value_is_unaffected()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, documentId) = await SeedAsync(connection);
        var fieldId = await AddFieldAsync(connection, tenantId, maskVersionId, FieldDataType.Text, isList: false);

        // The control for the test above: the refusal has to come from the TYPE, not from the value looking
        // odd. A Text field with no FormatPattern constrains nothing, exactly as before.
        using var context = Ctx(connection, tenantId);
        context.FieldValues.Add(Value(tenantId, documentId, fieldId, "events"));
        await context.SaveChangesAsync();

        Assert.Single(await context.FieldValues.Where(v => v.FieldDefinitionId == fieldId).ToListAsync());
    }

    [Fact]
    public async Task IsList_is_stored_as_written_in_both_directions()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var (tenantId, maskVersionId, _) = await SeedAsync(connection);
        var listField = await AddFieldAsync(connection, tenantId, maskVersionId, FieldDataType.Text, isList: true);

        using var context = Ctx(connection, tenantId);
        var single = new FieldDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskVersionId = maskVersionId,
            Name = "Keywords",
            DataType = FieldDataType.Text,
            IsList = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.FieldDefinitions.Add(single);
        await context.SaveChangesAsync();

        // The `false` direction is the one worth asserting. A model-level store default would make EF omit
        // the property whenever it equals the CLR default, so a field deliberately saved as single-valued
        // would silently take whatever the database decided — the trap the EF configuration comments about.
        using var reader = Ctx(connection, tenantId);
        Assert.True(await reader.FieldDefinitions.Where(f => f.Id == listField).Select(f => f.IsList).SingleAsync());
        Assert.False(await reader.FieldDefinitions.Where(f => f.Id == single.Id).Select(f => f.IsList).SingleAsync());
    }
}
