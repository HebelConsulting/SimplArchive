namespace SimplArchive.Application.Abstractions;

// Emails the in-app notifications that owe one (ADR "Email notifications (SMTP)"; queue shape ADR 0840). Run on
// a timer by EmailNotificationWorker; also callable directly (tests). Returns the number of notifications
// successfully emailed this pass.
//
// IT IS AN OUTBOX NOW, and this comment said the opposite — that email ran "off the existing Notification rows
// (EmailedAt bookkeeping) rather than a separate outbox". That was true and was the defect: the pending set was
// `EmailedAt == null && EmailFailedAt == null`, which cannot be compare-and-swapped without recording a failed
// send as sent, so two sweeps in two instances (ADR 0808) both sent. A row in `EmailOutbox` carries the claim
// instead. What the old note was protecting is unchanged and still holds: the queue row is written by
// SaveChanges for EVERY notification insert, so workflow / comment / ACL / escalation notifications are all
// still covered without any creation site knowing about email.
public interface IEmailNotificationDispatcher
{
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default);
}
