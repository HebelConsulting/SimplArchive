namespace SimplArchive.Application.Abstractions;

// Verifies the integrity of the current tenant's audit hash chain (ADR "Audit trail hash chain"): walks the
// events in Sequence order, recomputing each link, and reports the first break (an edited field, a deleted
// row, or a gap/reorder) if any. Tenant-scoped via the DbContext's tenant query filter, so it verifies the
// caller's tenant only.
public interface IAuditChainVerifier
{
    Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken = default);
}

// Valid = the chain recomputed cleanly. CheckedCount = events walked. BrokenAtSequence = the Sequence where
// the first mismatch/gap was found (null when Valid).
public sealed record AuditChainVerification(bool Valid, int CheckedCount, long? BrokenAtSequence);
