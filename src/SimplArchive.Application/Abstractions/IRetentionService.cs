namespace SimplArchive.Application.Abstractions;

// Auto-disposes documents whose records-retention period has elapsed (ADR "Retention policies
// (auto-disposition)"). A document is due when its assigned mask has a RetentionYears and
// (latest confirmed version's DocumentDate ?? CreatedAt) + RetentionYears has passed, it isn't under an active
// legal hold, and it isn't already deleted. Disposition = soft-delete to the recycle bin (reversible). Run by
// the hosted RetentionWorker; also callable directly (tests). Returns the number of documents disposed.
public interface IRetentionService
{
    Task<int> SweepAsync(CancellationToken cancellationToken = default);
}
