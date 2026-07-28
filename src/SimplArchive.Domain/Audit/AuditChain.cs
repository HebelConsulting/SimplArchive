namespace SimplArchive.Domain.Audit;

// Shared constants for the audit-log tamper-evidence hash chain (ADRs "Audit trail hash chain" and "Audit
// trail retention and purge"). Lives in the Domain so both the entity defaults (Tenant.AuditChainStart…) and
// the Infrastructure hasher/verifier share one source.
public static class AuditChain
{
    // The "previous hash" seed for the genesis event of a tenant's chain — a fixed 64-hex-char value.
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
}
