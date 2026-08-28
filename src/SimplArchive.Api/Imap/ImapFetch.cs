using System.Text;
using MimeKit;
using MimeKit.Utils;
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

        async Task<byte[]> BytesAsync() => bytes ??= await MessageBytesAsync(storage, message);

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
                case "EMAILID":
                    // RFC 8474 §4 (#780). The DOCUMENT's id, so the same document reached through its home
                    // folder and through a folder it is referenced into answers identically — which the RFC
                    // requires of a COPY, and our COPY files a reference.
                    parts.Add($"EMAILID ({ImapObjectId.ForMessage(message.DocumentId)})");
                    break;
                case "THREADID":
                    // RFC 8474 §6: "if the server ... is unable to calculate relationships between messages, it
                    // MUST return NIL". We have no threading model — the eMail mask's "Conversation ID" field
                    // exists but only the interop import fills it, and a synthetic message (a PDF served as mail)
                    // has no thread at all. NIL is both the conforming answer and the true one; inventing a
                    // per-message thread would be worse than saying nothing, because a client would BELIEVE it.
                    parts.Add("THREADID NIL");
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
                        var payload = await SectionAsync(session, section, BytesAsync, MimeAsync);

                        // The <start.count> partial (RFC 3501 §6.4.5). It was silently dropped: a client asking
                        // for the first 16 KB of a section got the whole thing, unlabeled — and a partial
                        // response MUST carry its origin octet (`BODY[TEXT]<0>`), or the client splices what it
                        // got at the wrong offset. Ignoring a qualifier the client sent is the same fault as
                        // refusing one it may send: it believes it asked and was answered.
                        var label = $"BODY[{section.ToUpperInvariant()}]";
                        var angle = item.IndexOf('<', item.LastIndexOf(']'));
                        if (angle >= 0 && item.EndsWith(">", StringComparison.Ordinal))
                        {
                            var range = item[(angle + 1)..^1].Split('.');
                            if (range.Length is 1 or 2
                                && long.TryParse(range[0], out var start) && start >= 0
                                && (range.Length == 1 || long.TryParse(range[1], out _)))
                            {
                                var count = range.Length == 2 ? long.Parse(range[1]) : long.MaxValue;
                                var from = (int)Math.Min(start, payload.Length);
                                var take = (int)Math.Min(count, payload.Length - from);
                                payload = payload[from..(from + take)];
                                label += $"<{start}>";
                            }
                        }

                        literals.Add(($"{label} {{{payload.Length}}}", payload));
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

        // Every data item after the first is separated by a SPACE, and that includes the one that follows a
        // literal's octets — the response is ONE line with binary spliced into it, not a sequence of lines.
        // Emitting the next item's prefix on its own left `<octets>BODY[TEXT] {45}` on the wire with nothing
        // between them, which a strict parser reads as one malformed atom. It only shows with TWO or more
        // sections in one FETCH, which is what a client asking for headers and body together does.
        var separator = parts.Count > 0 ? " " : string.Empty;
        foreach (var (prefix, payload) in literals)
        {
            await session.WriteLineAsync(head + separator + prefix);
            await session.WriteRawAsync(payload);
            head = string.Empty;
            separator = " ";
        }

        // No space before the closing paren — it may follow the octets directly.
        await session.WriteLineAsync(")");
    }

    private static async Task<byte[]> SectionAsync(
        ImapSession session, string section, Func<Task<byte[]>> bytesAsync, Func<Task<MimeMessage>> mimeAsync)
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

        // A NUMBERED section — "2", "2.1", "2.MIME", "1.TEXT". This is how a mail client downloads ONE part,
        // and an attachment is always a part: it is the path taken to save a PDF, where BODY[] is the path
        // taken to read a message. This used to answer with the WHOLE message, on the reasoning that numbered
        // sections were "rare from the clients this slice targets". They are not rare — they are how every
        // client saves an attachment — and the answer was not an error the client could report, so it wrote
        // the entire RFC-822 message to disk under the attachment's name and the user got a corrupt PDF
        // (#766). Serving it wrongly and silently is worse than refusing it.
        if (Numbered(upper, message) is { } part)
        {
            return part;
        }

        session.WarnSubstituted($"BODY[{section}]", "the whole message");
        return await bytesAsync();
    }

    /// <summary>
    /// One numbered body section, or <c>null</c> when the message has no such part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the part's content in the transfer encoding it is STORED in, not decoded: BODYSTRUCTURE
    /// announces the encoding and the encoded octet count, and the client decodes what it is given. Decoding
    /// here would corrupt the file just as thoroughly as the old answer did, only less obviously — the client
    /// would base64-decode plain bytes.
    /// </para>
    /// <para>
    /// RFC 3501's numbering, so section "1" of a NON-multipart message is the message's own body rather than a
    /// child that does not exist.
    /// </para>
    /// </remarks>
    private static byte[]? Numbered(string section, MimeMessage message)
    {
        var segments = section.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        // A trailing keyword — MIME, HEADER, TEXT — qualifies the part the digits located.
        var suffix = char.IsAsciiDigit(segments[^1][0]) ? string.Empty : segments[^1];
        var path = suffix.Length == 0 ? segments : segments[..^1];

        MimeEntity? entity = message.Body;
        foreach (var segment in path)
        {
            if (!int.TryParse(segment, out var index) || index < 1)
            {
                return null;
            }

            entity = Child(entity, index);
            if (entity is null)
            {
                return null;
            }
        }

        if (entity is null)
        {
            return null;
        }

        return suffix switch
        {
            "MIME" => Encoding.Latin1.GetBytes(string.Concat(entity.Headers.Select(h => $"{h.Field}: {h.Value}\r\n")) + "\r\n"),
            "HEADER" when entity is MessagePart { Message: { } inner } =>
                Encoding.Latin1.GetBytes(string.Concat(inner.Headers.Select(h => $"{h.Field}: {h.Value}\r\n")) + "\r\n"),
            "TEXT" when entity is MessagePart { Message: { } inner } => Body(inner.Body),
            "" => Body(entity),
            _ => null,
        };
    }

    // The child a section number names: within a multipart, within a nested message, or — for a leaf — the
    // leaf itself, which is what "1" means when the message is not multipart at all.
    private static MimeEntity? Child(MimeEntity? entity, int index) => entity switch
    {
        Multipart multipart => index <= multipart.Count ? multipart[index - 1] : null,
        MessagePart { Message: { } inner } => Child(inner.Body, index),
        not null when index == 1 => entity,
        _ => null,
    };

    // A part's BODY — its content without its own MIME headers, exactly as stored.
    private static byte[]? Body(MimeEntity? entity)
    {
        using var buffer = new MemoryStream();
        switch (entity)
        {
            case MimePart { Content: { } content }:
                content.WriteTo(buffer);
                return buffer.ToArray();

            // A multipart's body is its children and their boundaries — everything after its own headers.
            case Multipart or MessagePart:
                entity!.WriteTo(buffer);
                var raw = buffer.ToArray();
                var headerEnd = FindHeaderEnd(raw);
                return headerEnd < 0 ? raw : raw[headerEnd..];

            default:
                return null;
        }
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

    /// <summary>
    /// The RFC-822 bytes of one message: the stored <c>.eml</c> as filed, or the synthetic wrapper built around
    /// any other document.
    /// </summary>
    /// <remarks>
    /// Shared with SEARCH rather than reimplemented there, so the two cannot disagree about what a message IS.
    /// A search that matched on different bytes than the fetch returns would produce hits a user cannot find —
    /// the same class of silent wrongness that made SEARCH worth implementing in the first place.
    /// </remarks>
    internal static async Task<byte[]> MessageBytesAsync(IObjectStorageClient storage, ImapMessageEntry message)
    {
        if (!message.Extension.Equals(".eml", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildSyntheticAsync(storage, message);
        }

        await using var stream = await storage.GetObjectAsync(message.ObjectKey);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

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

        // CRLF explicitly (#802): MimeKit's default FormatOptions follow the PLATFORM (LF on Unix), and an
        // LF-only message is not RFC 5322. Most clients tolerated it on BODY[], but our own TEXT slicer scans
        // for CRLFCRLF to find the header end — so BODY[TEXT] of a synthetic message silently returned the
        // WHOLE message, headers, boundaries and base64 included, and a mail client rendered that soup as the
        // message text. The server's own parser was the first strict consumer of its own malformed output.
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        mime.WriteTo(options, output);
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

    /// <remarks>
    /// IMAP is a 7-bit protocol and a quoted string may not carry bare non-ASCII — yet the values quoted here
    /// come from MimeKit DECODED (a subject, a filename), so "invoice — January.pdf" put a raw em-dash on the
    /// wire (#802). Re-encoded as RFC 2047 words when needed: that is the form the header would carry in the
    /// message itself, and the form every client already decodes for display.
    /// </remarks>
    private static string Quote(string? value)
    {
        if (value is null)
        {
            return "NIL";
        }

        if (value.Any(c => c > 127))
        {
            value = Encoding.ASCII.GetString(Rfc2047.EncodeText(FormatOptions.Default, Encoding.UTF8, value));
        }

        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
