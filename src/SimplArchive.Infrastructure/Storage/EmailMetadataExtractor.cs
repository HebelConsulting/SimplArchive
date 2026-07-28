using MimeKit;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

// Parses .eml (MimeKit) / .msg (MSGReader) envelope fields for email auto-classification (ADR "Email
// auto-classification") — From/To/Cc/Subject/Date + the Message-ID (the "Entry ID" field). Header-only; the body
// is EmailConverter's concern (preview). Any parse failure yields null so the caller falls back to the
// default mask.
public class EmailMetadataExtractor : IEmailMetadataExtractor
{
    public Task<EmailMetadata?> ExtractAsync(Stream stream, string extension, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = extension.Equals(".msg", StringComparison.OrdinalIgnoreCase)
                ? ParseMsg(stream)
                : ParseEml(stream);
            return Task.FromResult<EmailMetadata?>(metadata);
        }
        catch (Exception)
        {
            return Task.FromResult<EmailMetadata?>(null);
        }
    }

    public Task<IReadOnlyList<EmailAttachment>> ExtractAttachmentsAsync(Stream stream, string extension, CancellationToken cancellationToken = default)
    {
        try
        {
            var attachments = extension.Equals(".msg", StringComparison.OrdinalIgnoreCase)
                ? ExtractMsgAttachments(stream)
                : ExtractEmlAttachments(stream);
            return Task.FromResult<IReadOnlyList<EmailAttachment>>(attachments);
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<EmailAttachment>>([]);
        }
    }

    // MimeKit's Attachments yields only Content-Disposition: attachment parts — inline (cid) parts live in the
    // body, so they're already excluded.
    private static List<EmailAttachment> ExtractEmlAttachments(Stream stream)
    {
        var message = MimeMessage.Load(stream);
        var result = new List<EmailAttachment>();

        foreach (var attachment in message.Attachments)
        {
            if (attachment is MessagePart { Message: { } nested })
            {
                using var memory = new MemoryStream();
                nested.WriteTo(memory);
                var fileName = attachment.ContentDisposition?.FileName;
                var name = string.IsNullOrWhiteSpace(fileName) ? $"{nested.Subject ?? "message"}.eml" : fileName;
                result.Add(new EmailAttachment(name, "message/rfc822", memory.ToArray()));
            }
            else if (attachment is MimePart { Content: { } partContent } part)
            {
                using var memory = new MemoryStream();
                partContent.DecodeTo(memory);
                result.Add(new EmailAttachment(part.FileName ?? "attachment", part.ContentType?.MimeType, memory.ToArray()));
            }
        }

        return result;
    }

    private static List<EmailAttachment> ExtractMsgAttachments(Stream stream)
    {
        using var message = new MsgReader.Outlook.Storage.Message(stream);
        var result = new List<EmailAttachment>();

        foreach (var obj in message.Attachments)
        {
            // Skip inline images and nested-message attachments (one level only, ADR "Email attachments as
            // child documents").
            if (obj is MsgReader.Outlook.Storage.Attachment attachment && !attachment.IsInline && attachment.Data is { } data)
            {
                result.Add(new EmailAttachment(attachment.FileName ?? "attachment", attachment.MimeType, data));
            }
        }

        return result;
    }

    private static EmailMetadata ParseEml(Stream stream)
    {
        var message = MimeMessage.Load(stream);

        return new EmailMetadata(
            From: message.From?.ToString() ?? string.Empty,
            To: message.To?.ToString() ?? string.Empty,
            Cc: message.Cc is { Count: > 0 } ? message.Cc.ToString() : null,
            Subject: message.Subject ?? string.Empty,
            Date: message.Headers.Contains(HeaderId.Date) ? message.Date : null,
            MessageId: NormalizeMessageId(message.MessageId));
    }

    private static EmailMetadata ParseMsg(Stream stream)
    {
        using var message = new MsgReader.Outlook.Storage.Message(stream);

        var to = string.Join(", ", message.Recipients
            .Where(r => r.Type == MsgReader.Outlook.RecipientType.To)
            .Select(r => FormatAddress(r.DisplayName, r.Email)));

        var cc = string.Join(", ", message.Recipients
            .Where(r => r.Type == MsgReader.Outlook.RecipientType.Cc)
            .Select(r => FormatAddress(r.DisplayName, r.Email)));

        string? messageId = null;
        try
        {
            // MSGReader parses the internet transport headers into Headers (null if the .msg carries none).
            messageId = message.Headers?.MessageId?.ToString();
        }
        catch (Exception)
        {
            // Some .msg files carry no transport headers — leave Entry ID empty (an optional field).
        }

        return new EmailMetadata(
            From: FormatAddress(message.Sender?.DisplayName, message.Sender?.Email),
            To: to,
            Cc: string.IsNullOrWhiteSpace(cc) ? null : cc,
            Subject: message.Subject ?? string.Empty,
            Date: message.SentOn,
            MessageId: NormalizeMessageId(messageId));
    }

    private static string FormatAddress(string? displayName, string? email)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return email ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(email) ? displayName : $"{displayName} <{email}>";
    }

    // The "Entry ID" is shown in <...> form (e.g. <C4BD621A…@ISSRVDE>). MimeKit's MessageId strips the angle
    // brackets; a raw header may keep them — normalise to exactly one pair.
    private static string? NormalizeMessageId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var inner = raw.Trim().TrimStart('<').TrimEnd('>').Trim();
        return inner.Length == 0 ? null : $"<{inner}>";
    }
}
