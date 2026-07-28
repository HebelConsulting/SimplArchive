namespace SimplArchive.Infrastructure.Notifications;

// SMTP configuration (ADR "Email notifications (SMTP)"), bound from the "Smtp" section. When Host is empty,
// email sending is disabled (NullEmailSender + no worker) — the same "unset → disabled" convention as
// Gotenberg / OpenSearch / OCR. Dev/demo points Host at the Mailpit sidecar (no auth, plain SMTP on :1025).
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    // A dev catcher like Mailpit speaks plain SMTP; a real server typically wants STARTTLS.
    public bool UseStartTls { get; set; }

    // Optional credentials — omitted for an unauthenticated dev catcher.
    public string? User { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "notifications@simplarchive.local";

    public string FromName { get; set; } = "SimplArchive";
}
