namespace SimplArchive.Application.Abstractions;

// Emails the in-app notifications that haven't been emailed yet (ADR "Email notifications (SMTP)"). Drives
// email off the existing Notification rows (EmailedAt bookkeeping) rather than a separate outbox, so workflow /
// comment / ACL / escalation notifications are all covered. Run on a timer by EmailNotificationWorker; also
// callable directly (tests). Returns the number of notifications successfully emailed this pass.
public interface IEmailNotificationDispatcher
{
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default);
}
