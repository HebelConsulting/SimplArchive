namespace SimplArchive.Application.Abstractions;

// Auto-releases check-outs that have sat idle past their tenant's threshold (ADR "Stale check-out
// auto-release sweep"). For each active tenant with CheckoutTtlDays > 0, a document whose CheckedOutAt is
// older than that many days has its lock cleared, its cloud working-copy stash deleted, a Document.CheckoutExpired
// audit event recorded, and its former holder notified (in-app + email). Idempotent — releasing clears
// CheckedOutAt, so a row can't be swept twice. Run by the hosted StaleCheckoutWorker; also callable directly
// (tests). Returns the number of check-outs released.
public interface IStaleCheckoutService
{
    Task<int> SweepAsync(CancellationToken cancellationToken = default);
}
