using System.Text;
using MimeKit;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Imap;

// FETCH / UID FETCH (ADR "IMAP endpoint (read-only, first slice)"): sequence-set parsing, the response items a
// real client asks for (FLAGS, UID, INTERNALDATE, RFC822.SIZE, ENVELOPE, BODYSTRUCTURE, BODY[<section>]), and
// the message materialization — a stored .eml serves raw, any other document becomes a synthetic message
// carrying the file as an attachment (#562, behind the user's ShowAllDocuments toggle).
internal static class ImapFetch
{
    internal static async Task FetchAsync(
        ImapSession session, IServiceScope scope, string tag, ImapSelectedMailbox selected, string arguments, bool uidMode)
    {
        var setEnd = arguments.IndexOf(' ');
        if (setEnd < 0)
        {
            await session.WriteLineAsync($"{tag} BAD FETCH expects a set and items");
            return;
        }

        var set = arguments[..setEnd];
        var items = ParseItems(arguments[(setEnd + 1)..], uidMode);
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var seen = await ImapMailboxes.SeenSetAsync(scope, selected.Messages);

        for (var index = 0; index < selected.Messages.Count; index++)
        {
            var message = selected.Messages[index];
            var sequence = index + 1;
            if (!InSet(set, uidMode ? message.Uid : sequence, uidMode ? LastUid(selected) : selected.Messages.Count))
            {
                continue;
            }

            // A non-PEEK body fetch implicitly sets \Seen (RFC 3501 §6.4.5) — recorded BEFORE the response so
            // the FLAGS item in the same reply already reflects it.
            if (items.Any(i => i.StartsWith("BODY[", StringComparison.OrdinalIgnoreCase)) && !seen.Contains(message.DocumentId))
            {
                await ImapMailboxes.MarkSeenAsync(scope, message.DocumentId, seen: true);
                seen.Add(message.DocumentId);
            }

            await WriteMessageAsync(session, storage, message, sequence, items, seen.Contains(message.DocumentId),
                selected.DeletedDocumentIds.Contains(message.DocumentId));
        }

        await session.OkAsync(tag, uidMode ? "UID FETCH" : "FETCH");
    }

    private static int LastUid(ImapSelectedMailbox selected) =>
        selected.Messages.Count == 0 ? 0 : selected.Messages[^1].Uid;

    // ---- Sequence sets -------------------------------------------------------------------------------

    internal static bool InSet(string set, int value, int star)
    {
        foreach (var part in set.Split(','))
        {
            var range = part.Split(':');
            var from = ParseBound(range[0], star);
            var to = range.Length > 1 ? ParseBound(range[1], star) : from;
            if (value >= Math.Min(from, to) && value <= Math.Max(from, to))
            {
                return true;
            }
        }

        return false;
    }

    private static int ParseBound(string bound, int star) =>
        bound == "*" ? star : int.TryParse(bound, out var n) ? n : -1;

    // ---- Items ---------------------------------------------------------------------------------------

    private static List<string> ParseItems(string raw, bool uidMode)
    {
        raw = raw.Trim();
        if (raw.StartsWith('(') && raw.EndsWith(')'))
        {
            raw = raw[1..^1];
        }

        // Macros first (RFC 3501 §6.4.5), then split — BODY[...] sections may contain spaces (HEADER.FIELDS
        // lists), so splitting respects brackets.
        var upper = raw.ToUpperInvariant();
        raw = upper switch
        {
            "ALL" => "FLAGS INTERNALDATE RFC822.SIZE ENVELOPE",
            "FAST" => "FLAGS INTERNALDATE RFC822.SIZE",
            "FULL" => "FLAGS INTERNALDATE RFC822.SIZE ENVELOPE BODY",
            _ => raw,
        };

        var items = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i == raw.Length || (raw[i] == ' ' && depth == 0))
            {
                if (i > start)
                {
                    items.Add(raw[start..i]);
                }

                start = i + 1;
            }
            else if (raw[i] is '[' or '(')
            {
                depth++;
            }
            else if (raw[i] is ']' or ')')
            {
                depth--;
            }
        }

        if (uidMode && !items.Any(i => i.Equals("UID", StringComparison.OrdinalIgnoreCase)))
        {
            items.Add("UID");
        }

        return items;
    }

    // ---- Response ------------------------------------------------------------------------------------

    private static async Task WriteMessageAsync(
        ImapSession session, IObjectStorageClient storage, ImapMessageEntry message, int sequence, List<string> items, bool seen, bool deleted)
    {
        byte[]? bytes = null;
        MimeMessage? mime = null;

        async Task<byte[]> BytesAsync()
        {
            if (bytes is null)
            {
                if (message.Extension.Equals(".eml", StringComparison.OrdinalIgnoreCase))
                {
                    await using var stream = await storage.GetObjectAsync(message.ObjectKey);
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    bytes = buffer.ToArray();
                }
                else
                {
                    bytes = await BuildSyntheticAsync(storage, message);
                }
            }

            return bytes;
        }

        async Task<MimeMessage> MimeAsync() => mime ??= MimeMessage.Load(new MemoryStream(await BytesAsync()));

        var parts = new List<string>();
        var literals = new List<(string Prefix, byte[] Payload)>();

        foreach (var item in items)
        {
            var upper = item.ToUpperInvariant();
            switch (upper)
            {
                case "FLAGS":
                    // Persisted read state (slice 2) + the session's \Deleted staging (slice 3).
                    parts.Add($"FLAGS ({string.Join(' ', new[] { seen ? "\\Seen" : null, deleted ? "\\Deleted" : null }.Where(f => f is not null))})");
                    break;
                case "UID":
                    parts.Add($"UID {message.Uid}");
                    break;
                case "INTERNALDATE":
                    parts.Add($"INTERNALDATE \"{message.InternalDate.ToUniversalTime():dd-MMM-yyyy HH:mm:ss} +0000\"");
                    break;
                case "RFC822.SIZE":
                    // Exact for a stored .eml (the version's byte size); a synthetic message serializes to be
                    // measured — the honest cost of fabricating it (#562, noted in the ADR).
                    parts.Add(message.Extension.Equals(".eml", StringComparison.OrdinalIgnoreCase) && message.SizeBytes is { } size
                        ? $"RFC822.SIZE {size}"
                        : $"RFC822.SIZE {(await BytesAsync()).Length}");
                    break;
                case "ENVELOPE":
                    parts.Add($"ENVELOPE {Envelope(await MimeAsync())}");
                    break;
                case "BODY":
                case "BODYSTRUCTURE":
                    parts.Add($"{upper} {BodyStructure((await MimeAsync()).Body, extended: upper == "BODYSTRUCTURE")}");
                    break;
                default:
                    if (upper.StartsWith("BODY.PEEK[") || upper.StartsWith("BODY["))
                    {
                        var open = item.IndexOf('[');
                        var section = item[(open + 1)..item.LastIndexOf(']')];
                        var payload = await SectionAsync(section, BytesAsync, MimeAsync);
                        literals.Add(($"BODY[{section.ToUpperInvariant()}] {{{payload.Length}}}", payload));
                    }
                    else
                    {
                        // An unknown item is skipped rather than failing the whole FETCH — clients vary.
                    }

                    break;
            }
        }

        var head = $"* {sequence} FETCH ({string.Join(' ', parts)}";
        if (literals.Count == 0)
        {
            await session.WriteLineAsync(head + ")");
            return;
        }

        var separator = parts.Count > 0 ? " " : string.Empty;
        foreach (var (prefix, payload) in literals)
        {
            await session.WriteLineAsync(head + separator + prefix);
            await session.WriteRawAsync(payload);
            head = string.Empty;
            separator = string.Empty;
        }

        await session.WriteLineAsync(")");
    }

    private static async Task<byte[]> SectionAsync(string section, Func<Task<byte[]>> bytesAsync, Func<Task<MimeMessage>> mimeAsync)
    {
        var upper = section.ToUpperInvariant();
        if (upper.Length == 0)
        {
            return await bytesAsync();
        }

        var message = await mimeAsync();
        if (upper == "HEADER")
        {
            return Encoding.Latin1.GetBytes(string.Concat(message.Headers.Select(h => $"{h.Field}: {h.Value}\r\n")) + "\r\n");
        }

        if (upper.StartsWith("HEADER.FIELDS"))
        {
            var open = section.IndexOf('(');
            var wanted = section[(open + 1)..section.LastIndexOf(')')]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.ToUpperInvariant())
                .ToHashSet();
            var selected = message.Headers.Where(h => wanted.Contains(h.Field.ToUpperInvariant()));
            return Encoding.Latin1.GetBytes(string.Concat(selected.Select(h => $"{h.Field}: {h.Value}\r\n")) + "\r\n");
        }

        if (upper == "TEXT")
        {
            var raw = await bytesAsync();
            var headerEnd = FindHeaderEnd(raw);
            return headerEnd < 0 ? raw : raw[headerEnd..];
        }

        // Numbered part sections are rare from the clients this slice targets; answer with the full body.
        return await bytesAsync();
    }

    private static int FindHeaderEnd(byte[] raw)
    {
        for (var i = 0; i + 3 < raw.Length; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
            {
                return i + 4;
            }
        }

        return -1;
    }

    // ---- Synthetic messages --------------------------------------------------------------------------

    private static async Task<byte[]> BuildSyntheticAsync(IObjectStorageClient storage, ImapMessageEntry message)
    {
        await using var stream = await storage.GetObjectAsync(message.ObjectKey);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("SimplArchive", "no-reply@simplarchive.local"));
        mime.Subject = message.Name + message.Extension;
        mime.Date = message.InternalDate;
        // Stable per document — clients dedupe by Message-ID, and a regenerated synthetic must be the SAME message.
        mime.MessageId = $"{message.DocumentId}@simplarchive";

        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = $"{message.Name}{message.Extension} — served from the SimplArchive archive." },
            new MimePart
            {
                Content = new MimeContent(new MemoryStream(content.ToArray())),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = message.Name + message.Extension },
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = message.Name + message.Extension,
            },
        };
        mime.Body = body;

        using var output = new MemoryStream();
        mime.WriteTo(output);
        return output.ToArray();
    }

    // ---- ENVELOPE / BODYSTRUCTURE --------------------------------------------------------------------

    private static string Envelope(MimeMessage m) =>
        "(" + string.Join(' ',
            Quote(m.Date == default ? null : m.Date.ToString("dd-MMM-yyyy HH:mm:ss zz00")),
            Quote(m.Subject),
            Addresses(m.From.Mailboxes),
            Addresses((m.Sender is null ? m.From.Mailboxes : [m.Sender])),
            Addresses(m.ReplyTo.Mailboxes.Any() ? m.ReplyTo.Mailboxes : m.From.Mailboxes),
            Addresses(m.To.Mailboxes),
            Addresses(m.Cc.Mailboxes),
            Addresses(m.Bcc.Mailboxes),
            Quote(m.InReplyTo is { Length: > 0 } irt ? $"<{irt}>" : null),
            Quote(m.MessageId is { Length: > 0 } id ? $"<{id}>" : null)) + ")";

    private static string Addresses(IEnumerable<MailboxAddress> addresses)
    {
        var list = addresses.ToList();
        return list.Count == 0
            ? "NIL"
            : "(" + string.Concat(list.Select(a => $"({Quote(string.IsNullOrEmpty(a.Name) ? null : a.Name)} NIL {Quote(a.LocalPart)} {Quote(a.Domain)})")) + ")";
    }

    private static string BodyStructure(MimeEntity? entity, bool extended)
    {
        switch (entity)
        {
            case Multipart multipart:
                {
                    var children = string.Concat(multipart.Select(c => BodyStructure(c, extended)));
                    return $"({children} {Quote(multipart.ContentType.MediaSubtype.ToUpperInvariant())})";
                }
            case MessagePart:
                // A message/rfc822 part serves as an opaque leaf in this slice.
                return "(\"MESSAGE\" \"RFC822\" NIL NIL NIL \"7BIT\" 0)";
            case MimePart part:
                {
                    var parameters = part.ContentType.Parameters.Count == 0
                        ? "NIL"
                        : "(" + string.Join(' ', part.ContentType.Parameters.Select(p => $"{Quote(p.Name.ToUpperInvariant())} {Quote(p.Value)}")) + ")";
                    var encoding = part.ContentTransferEncoding switch
                    {
                        ContentEncoding.Base64 => "BASE64",
                        ContentEncoding.QuotedPrintable => "QUOTED-PRINTABLE",
                        ContentEncoding.EightBit => "8BIT",
                        _ => "7BIT",
                    };
                    var size = part.Content?.Stream?.Length ?? 0;
                    var lineEstimate = part.ContentType.IsMimeType("text", "*") ? $" {Math.Max(1, size / 60)}" : string.Empty;
                    return $"({Quote(part.ContentType.MediaType.ToUpperInvariant())} {Quote(part.ContentType.MediaSubtype.ToUpperInvariant())} {parameters} NIL NIL {Quote(encoding)} {size}{lineEstimate})";
                }
            default:
                return "(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"US-ASCII\") NIL NIL \"7BIT\" 0 0)";
        }
    }

    private static string Quote(string? value) =>
        value is null ? "NIL" : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
