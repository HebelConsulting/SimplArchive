namespace SimplArchive.Application.Abstractions;

// Envelope metadata parsed from an .eml/.msg file for email auto-classification (ADR "Email
// auto-classification"). From/To/Subject may be empty (the caller placeholders them, since the eMail mask
// requires them); Cc/Date/MessageId are absent on many messages. MessageId is the "Entry ID" field
// (the RFC 5322 Message-ID, normalised to <...> form).
public sealed record EmailMetadata(
    string From,
    string To,
    string? Cc,
    string Subject,
    DateTimeOffset? Date,
    string? MessageId);

// A regular (non-inline) email attachment (ADR "Email attachments as child documents") — its filename,
// content type, and decoded bytes.
public sealed record EmailAttachment(string FileName, string? ContentType, byte[] Content);

// Parses an email file into EmailMetadata. Returns null if the bytes can't be parsed as the given format —
// the caller then falls back to the default (Basic Entry) mask.
public interface IEmailMetadataExtractor
{
    Task<EmailMetadata?> ExtractAsync(Stream stream, string extension, CancellationToken cancellationToken = default);

    // The message's regular attachments (inline / cid images are skipped). Empty on a parse failure.
    Task<IReadOnlyList<EmailAttachment>> ExtractAttachmentsAsync(Stream stream, string extension, CancellationToken cancellationToken = default);
}
