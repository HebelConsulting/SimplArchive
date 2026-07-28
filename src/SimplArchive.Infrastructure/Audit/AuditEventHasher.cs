using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SimplArchive.Domain.Audit;

namespace SimplArchive.Infrastructure.Audit;

// Computes the per-tenant hash-chain link for an audit event (ADR "Audit trail hash chain"). Shared by the
// recorder (which sets Hash at write time) and the verifier (which recomputes + compares). A link is
// SHA-256(previousHash + canonical(event fields)); the genesis event of a tenant's chain uses the fixed
// Genesis seed. Any edit to a field, a deleted row, or a reordered row breaks the recomputation downstream.
public static class AuditEventHasher
{
    // The "previous hash" of the first event in a tenant's chain (the Domain-shared genesis seed). A fixed
    // non-null value so the unique (TenantId, Sequence) index and the chain walk both treat genesis uniformly.
    public const string Genesis = Domain.Audit.AuditChain.GenesisHash;

    // Field/record separators — control chars that don't occur in real names/actions/details, so the canonical
    // form is unambiguous. (Even if one appeared, this is only hash input; there's no security boundary here.)
    private const char FieldSeparator = '\u001f';  // unit separator
    private const char RecordSeparator = '\u001e'; // record separator

    public static string ComputeHash(string previousHash, AuditEvent e)
    {
        // The Timestamp is truncated to microseconds at write time (TruncateToMicroseconds) so this canonical
        // form is identical before storing and after the Postgres/SQLite round-trip — else verification would
        // false-fail on lost sub-microsecond precision.
        var canonical = string.Join(FieldSeparator,
            e.Id.ToString("N"),
            e.TenantId.ToString("N"),
            e.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ((int)e.ActorType).ToString(CultureInfo.InvariantCulture),
            e.ActorId.ToString("N"),
            e.ActorName,
            e.Action,
            e.TargetType ?? "",
            e.TargetId?.ToString("N") ?? "",
            e.TargetName ?? "",
            e.Details ?? "");

        var input = previousHash + RecordSeparator + canonical;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    // Floors a timestamp to a microsecond boundary (10 ticks). Postgres timestamptz keeps microseconds;
    // SQLite stores the full DateTimeOffset text — truncating first makes the stored value round-trip to the
    // exact value that was hashed on both providers.
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);
}
