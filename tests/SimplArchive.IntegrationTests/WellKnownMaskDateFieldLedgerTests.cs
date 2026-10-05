using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

/// <summary>
/// Every <c>Date</c> field on a well-known mask says why it is a DAY and not a moment (owner, 2026-10-05).
/// </summary>
/// <remarks>
/// <para>
/// A <c>Date</c> field keeps the day and discards the clock, and nothing downstream can notice: the value
/// validates, displays and searches as a perfectly good date. That is how a certificate's validity, an
/// e-mail's <c>Date</c> header and a note's modification time all came to be stored as bare days — each one an
/// instant with a time and a zone, typed as a calendar day when its field was added. No rule was broken; a
/// field was ADDED, and nothing asked what it was.
/// </para>
/// <para>
/// So this asks. It is the mask-field counterpart of <c>DateOnlyTimeOnlyRatchetTests</c>, which covers the
/// domain's <c>DateOnly</c> properties and could not see any of the three, because none of them was one. The
/// fields come from a REAL seeded tenant rather than a scan of the seeder's source, so a field arriving through
/// any path — a new mask, a heal, a renamed one — is seen.
/// </para>
/// </remarks>
public sealed class WellKnownMaskDateFieldLedgerTests
{
    /// <summary>Every well-known <c>Date</c> field, keyed <c>Mask.Field</c>, with why it is a day.</summary>
    private static readonly Dictionary<string, string> Days = new(StringComparer.Ordinal)
    {
        ["License.Valid until"] =
            "The end of the support contract — a calendar DAY the vendor signed (TenantLicense.SupportContractEnd "
            + "is a DateOnly in the licence itself), so there is no time to lose.",
    };

    [Fact]
    public async Task Every_date_field_on_a_well_known_mask_says_why_it_is_a_day_and_not_a_moment()
    {
        var found = await DateFieldsAsync();

        // ANTI-VACUOUS: a seed that stopped producing masks would leave nothing to check and pass.
        Assert.NotEmpty(found);

        var undeclared = found.Where(f => !Days.ContainsKey(f)).ToList();
        Assert.True(undeclared.Count == 0,
            "These well-known mask fields are typed Date, and nothing says why they are a day:\n"
            + string.Join("\n", undeclared.Select(f => $"  {f}"))
            + "\n\nA Date field DISCARDS the time, silently — the stored value still validates and displays. Decide:\n"
            + "  • is it a MOMENT (a validity, a header, a modification, a revocation)?  Then make it DateTime and "
            + "write it with IndexInstant.Store, offset included.\n"
            + "  • is it a calendar DAY a person or a contract chose?  Then add it to Days above, saying so.");
    }

    [Fact]
    public async Task The_ledger_names_no_field_that_is_no_longer_a_date()
    {
        var found = await DateFieldsAsync();
        var stale = Days.Keys.Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "These entries name fields that are no longer Date fields on a well-known mask:\n"
            + string.Join("\n", stale.Select(s => $"  {s}"))
            + "\n\nRemove them — an answer to a question nobody is asking any more stops being read.");
    }

    private static async Task<HashSet<string>> DateFieldsAsync()
    {
        var tenantId = Guid.NewGuid();
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = tenantId };
        using var db = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, accessor);
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Ledger", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);

        var rows = await db.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == tenantId && v.IsCurrent)
            .Join(db.FieldDefinitions.IgnoreQueryFilters(), v => v.Id, f => f.MaskVersionId,
                (v, f) => new { Mask = v.Name, f.Name, f.DataType })
            .ToListAsync();

        return rows.Where(r => r.DataType == FieldDataType.Date).Select(r => $"{r.Mask}.{r.Name}").ToHashSet(StringComparer.Ordinal);
    }
}
