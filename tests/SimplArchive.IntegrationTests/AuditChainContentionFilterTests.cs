using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimplArchive.Api.Logging;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;
using Xunit;

namespace SimplArchive.IntegrationTests;

// Issue #759: the audit chain's designed contention is reported by EF at Error, which teaches an operator to
// ignore ERR on this service. The filter excludes exactly that signature and nothing else -- so what is worth
// testing is both halves of "exactly": that it recognises the real thing, and that it leaves everything near it
// alone.
public class AuditChainContentionFilterTests
{
    private static PostgresException Duplicate(string constraint) =>
        new("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505", constraintName: constraint);

    [Fact]
    public void Recognises_a_lost_race_for_a_chain_sequence()
    {
        Assert.True(AuditChainContentionFilter.IsDesignedContention(
            Duplicate(AuditChainContentionFilter.SequenceIndexName)));
    }

    // The Database.Command line logs the PostgresException directly; the Update line wraps it. Both must match,
    // which is why the filter walks the chain instead of looking at one depth.
    [Fact]
    public void Recognises_it_when_wrapped_by_the_save_changes_failure()
    {
        var wrapped = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            Duplicate(AuditChainContentionFilter.SequenceIndexName));

        Assert.True(AuditChainContentionFilter.IsDesignedContention(wrapped));
    }

    // The whole point of matching the index by name: a duplicate key anywhere else is a real fault and keeps its
    // Error. If this ever passes with a loose match, the filter has started hiding other people's bugs.
    [Fact]
    public void Leaves_a_duplicate_key_on_another_index_alone()
    {
        Assert.False(AuditChainContentionFilter.IsDesignedContention(
            Duplicate("IX_Documents_TenantId_ParentId_Name")));
    }

    [Fact]
    public void Leaves_another_failure_on_the_same_index_alone()
    {
        var deadlock = new PostgresException(
            "deadlock detected", "ERROR", "ERROR", "40P01",
            constraintName: AuditChainContentionFilter.SequenceIndexName);

        Assert.False(AuditChainContentionFilter.IsDesignedContention(deadlock));
    }

    [Fact]
    public void Leaves_an_event_with_no_exception_alone()
    {
        Assert.False(AuditChainContentionFilter.IsDesignedContention(null));
        Assert.False(AuditChainContentionFilter.IsDesignedContention(new InvalidOperationException("boom")));
    }

    // The failure mode this guard exists for. The filter keys on a database index NAME, so if the model ever
    // renames that index the filter silently matches nothing -- no error, no warning, just the ERR lines
    // quietly returning. That fails in the reassuring direction, so it is pinned to the model rather than to a
    // second copy of the literal.
    [Fact]
    public void The_index_name_still_matches_the_model()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        using var db = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor());

        var index = Assert.Single(
            db.Model.FindEntityType(typeof(AuditEvent))!.GetIndexes(),
            i => i.IsUnique
                 && i.Properties.Select(p => p.Name).SequenceEqual(
                        [nameof(AuditEvent.TenantId), nameof(AuditEvent.Sequence)]));

        Assert.Equal(AuditChainContentionFilter.SequenceIndexName, index.GetDatabaseName());
    }
}
