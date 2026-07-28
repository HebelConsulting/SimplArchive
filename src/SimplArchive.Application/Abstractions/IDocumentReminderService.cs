namespace SimplArchive.Application.Abstractions;

// Fires due document reminders (ADR "Document reminders"): notifies each reminder's target on the due date,
// then stamps a one-shot done (FiredAt) or advances a recurring one to its next occurrence. Runs across all
// tenants off the request path; the hosted DocumentReminderWorker calls it on a timer. Returns how many
// reminders fired.
public interface IDocumentReminderService
{
    Task<int> SweepAsync(CancellationToken cancellationToken = default);
}
