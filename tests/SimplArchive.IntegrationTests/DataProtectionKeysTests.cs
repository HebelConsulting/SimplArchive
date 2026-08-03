using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The Data Protection key store (ADR 0514): keys are persisted in the DataProtectionKeys table so antiforgery/auth
// cookies survive an API restart and are shared across replicas (fixing the "first login fails after a restart"
// bug). Verifies the DbContext maps IDataProtectionKeyContext's table and round-trips a key.
public class DataProtectionKeysTests
{
    private static SimplArchiveDbContext NewContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    [Fact]
    public async Task DataProtectionKeys_table_persists_and_round_trips()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setup = NewContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using (var write = NewContext(connection))
        {
            write.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = "key-1", Xml = "<key>…</key>" });
            await write.SaveChangesAsync();
        }

        using var read = NewContext(connection);
        var key = await read.DataProtectionKeys.SingleAsync();
        Assert.Equal("key-1", key.FriendlyName);
        Assert.Equal("<key>…</key>", key.Xml);
    }
}
