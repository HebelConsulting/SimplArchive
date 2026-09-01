using Npgsql;

namespace SimplArchive.Api.Logging;

/// <summary>
/// Recognises the audit hash chain's DESIGNED contention path, which EF Core reports at Error (issue #759).
/// </summary>
/// <remarks>
/// <para>
/// <c>AuditRecorder.AppendAsync</c> reads the chain tip, sets <c>Sequence = tip + 1</c>, and lets the unique
/// <c>(TenantId, Sequence)</c> index arbitrate concurrent same-tenant appends — the loser retries against the
/// new tip. Nothing is wrong when that happens: two users acted at the same moment, and the write succeeds on
/// the retry. But the losing INSERT raises <c>23505</c>, and EF's own
/// <c>Microsoft.EntityFrameworkCore.Database.Command</c> and <c>.Update</c> loggers report it at <b>Error</b> —
/// the level ADR 0430 reserves for "an exception for an admin to investigate".
/// </para>
/// <para>
/// The cost is not noise for its own sake: an operator who learns these are ignorable learns to ignore
/// <c>ERR</c> on this service, and a genuine audit-write failure then reads the same way. So those events are
/// excluded from the log, matched by the exception's own signature — the SQLSTATE and the one index — never by
/// source context, which would demote every unrelated database error along with them.
/// </para>
/// <para>
/// This hides a REAL duplicate key on that index too, and that is deliberate rather than overlooked: the case
/// that matters is the one the retries cannot absorb, and <c>AuditRecorder</c> logs that itself at Error when it
/// exhausts its attempts, naming the audit chain and the event that was lost. Nothing genuinely broken becomes
/// silent — it stops being reported by the logger that cannot tell the two apart.
/// </para>
/// </remarks>
public static class AuditChainContentionFilter
{
    /// <summary>
    /// The unique index the chain uses to arbitrate concurrent appends. Named exactly, so a duplicate key on any
    /// OTHER index keeps its Error — and pinned against the EF model by a test, because a rename here would make
    /// this filter match nothing at all, which fails silently and in the reassuring direction.
    /// </summary>
    public const string SequenceIndexName = "IX_AuditEvents_TenantId_Sequence";

    private const string UniqueViolation = "23505";

    /// <summary>True when the exception is a lost race for a chain sequence, rather than a fault.</summary>
    public static bool IsDesignedContention(Exception? exception)
    {
        // The two EF lines carry the same fault at different depths: Database.Command logs the PostgresException
        // directly, Update wraps it in a DbUpdateException. Walk the chain rather than picking a depth.
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException { SqlState: UniqueViolation, ConstraintName: SequenceIndexName })
            {
                return true;
            }
        }

        return false;
    }
}
