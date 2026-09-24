using System.Globalization;
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
        var envelope = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient>();
        var selfEnveloper = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Encryption.SmimeMessageEnveloper>();
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

            await WriteMessageAsync(session, storage, envelope, selfEnveloper, message, sequence, items,
                seen.Contains(message.DocumentId), selected.DeletedDocumentIds.Contains(message.DocumentId));
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
        ImapSession session, IObjectStorageClient storage, SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient envelope,
        SimplArchive.Infrastructure.Encryption.SmimeMessageEnveloper selfEnveloper,
        ImapMessageEntry message, int sequence, List<string> items, bool seen, bool deleted)
    {
        byte[]? bytes = null;
        MimeMessage? mime = null;

        // The envelope hook lives INSIDE the one funnel every FETCH view derives from — BODY[], the header
        // slices, BODYSTRUCTURE, the synthetic-size path — so a client can never see an enveloped body under
        // a plaintext BODYSTRUCTURE or vice versa. A split there is the #1158 family of bug: two views of
        // one message disagreeing, and only some clients caring. SEARCH deliberately keeps the PLAINTEXT
        // bytes (see ImapSearch): the server holds plaintext anyway, and matching against ciphertext would
        // silently turn every content search into "no results".
        //
        // Precedence (#1332): the user's own stored certificate envelopes IN-PROCESS and wins over the
        // sidecar hook — where both exist, the self-service certificate is the more specific claim about
        // what this user's devices can open. Both fail open to plaintext, each with its own Warning.
        async Task<byte[]> BytesAsync() => bytes ??=
            (session.SmimeCertificatePem is { } pem
                ? selfEnveloper.TryEnvelope(await MessageBytesAsync(storage, message), pem, session.Email)
                : await envelope.TryEnvelopeAsync(session.TenantName, session.Email, await MessageBytesAsync(storage, message), CancellationToken.None))
            ?? bytes ?? await MessageBytesAsync(storage, message);

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
                    // measured — the honest cost of fabricating it (#562, noted in the ADR). With an encryption
                    // service configured the stored size describes the PLAINTEXT, so the shortcut would lie
                    // about every enveloped message — measure the served bytes instead, unconditionally there,
                    // because whether THIS user's message envelopes depends on a cert lookup the shortcut
                    // cannot see.
                    parts.Add(!envelope.EnabledFor(session.TenantName) && session.SmimeCertificatePem is null
                        && message.Extension.Equals(".eml", StringComparison.OrdinalIgnoreCase) && message.SizeBytes is { } size
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
            // The message's OWN serialized header block, not a list rebuilt from message.Headers — and that
            // distinction is the whole bug behind "attachments never show in macOS Mail" (#1158). MimeKit keeps
            // a MimeMessage's Content-Type on the BODY entity, NOT in message.Headers, so a header rebuilt from
            // message.Headers carried MIME-Version but NO `Content-Type: multipart/mixed; boundary=…`. Per
            // RFC 2045 a message with no Content-Type IS text/plain, so a client that parses structure from the
            // header — macOS Mail does — saw a plain-text message, showed BODY[1], and never rendered the
            // attachment. iPad trusts BODYSTRUCTURE instead, which is why the SAME message worked there and hid
            // every attachment (of every size) on macOS. Serving raw[..headerEnd] gives the real header the
            // whole-message serializer already writes — Content-Type included — so both parsing strategies agree.
            var raw = await bytesAsync();
            var end = FindHeaderEnd(raw);
            return end < 0 ? raw : raw[..end];
        }

        if (upper.StartsWith("HEADER.FIELDS"))
        {
            // Filter the RAW header block, not message.Headers, for the same reason as HEADER above: a client
            // asking HEADER.FIELDS (Content-Type …) must get the body's Content-Type, which message.Headers omits.
            var open = section.IndexOf('(');
            var wanted = section[(open + 1)..section.LastIndexOf(')')]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.ToUpperInvariant())
                .ToHashSet();
            var raw = await bytesAsync();
            var end = FindHeaderEnd(raw);
            var headerBlock = Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]);
            var kept = new StringBuilder();
            foreach (var line in UnfoldHeaderLines(headerBlock))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && wanted.Contains(line[..colon].Trim().ToUpperInvariant()))
                {
                    kept.Append(line).Append("\r\n");
                }
            }

            return Encoding.Latin1.GetBytes(kept.Append("\r\n").ToString());
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

    // Header lines, unfolded (RFC 5322 §2.2.3): a line beginning with SP/HTAB continues the previous field,
    // so a folded Content-Type stays one logical line when HEADER.FIELDS filters by field name.
    private static IEnumerable<string> UnfoldHeaderLines(string headerBlock)
    {
        string? current = null;
        foreach (var line in headerBlock.Split("\r\n"))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if ((line[0] == ' ' || line[0] == '\t') && current is not null)
            {
                current += "\r\n" + line;
            }
            else
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = line;
            }
        }

        if (current is not null)
        {
            yield return current;
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

        // THE ENVELOPE REFLECTS THE DETAIL PANE (#1301), because it is the only part of the message a mail
        // client shows in its LIST — the columns a reader scans before opening anything. Fixed values there
        // made every document in the archive appear to be from "SimplArchive" on its filing date, which sorts
        // and groups by facts the pane does not present and the reader does not care about.
        //
        //   From    ← Created by        the person who filed it, as the pane names them
        //   Date    ← Document date     the record's OWN date, which is what "when is this from?" means
        //   Subject ← Name              without the extension, which is its own row
        //
        // The address half is synthesised (the archive's domain), because a display name is not an address and
        // the person may have none — the NAME is what a client renders, and that is what carries the meaning.
        var mime = new MimeMessage();
        mime.From.Add(message.Details?.CreatedBy is { Length: > 0 } author
            ? new MailboxAddress(author, "no-reply@simplarchive.local")
            : new MailboxAddress("SimplArchive", "no-reply@simplarchive.local"));
        mime.Subject = message.Name;
        // THE DATE IS THE PAIR, not the date alone. Taking only DocumentDate put every message at midnight,
        // which a mail client sorts and groups by — so the five METARs a weather folder collects in a day all
        // claimed the same instant and arrived in an arbitrary order. The time is stored UTC, and the header
        // carries its offset, so this is the document's own instant rather than a rendering of it.
        mime.Date = message.Details is { } details
            ? new DateTimeOffset(
                details.DocumentDate.ToDateTime(details.DocumentTime ?? TimeOnly.MinValue), TimeSpan.Zero)
            : message.InternalDate;
        // Stable per document — clients dedupe by Message-ID, and a regenerated synthetic must be the SAME
        // message. Self-identifying (#782): a re-filed export of this wrapper is recognised by SyntheticMessageId
        // and offered as a reference to the original rather than becoming a silent duplicate.
        mime.MessageId = SyntheticMessageId.For(message.DocumentId);

        // The same values the body renders, as headers a client can filter on. Header-safe by construction:
        // MimeKit would fold or reject an embedded newline, so every value is flattened to one line.
        foreach (var (name, value) in DetailHeaders(message))
        {
            mime.Headers.Add(name, value);
        }

        var mimeType = MimeTypes.GetMimeType(message.Name + message.Extension);

        // A TEXT document's content becomes the message BODY, inline, rather than an attachment behind the
        // signature (#1153). The reason is a measured Apple Mail behaviour and the
        // shape of what these documents ARE: a NOTAM briefing, a .txt, a .csv is text a reader came to read,
        // and Apple fetches a message whole only up to ~18 KB — above that it fetches per-part, takes the
        // text part to display inline, and DEFERS the attachment, showing no affordance for it at all. So a
        // large briefing filed as an attachment rendered as our footer and nothing else: the one thing the
        // user wanted was the one part Apple never fetched. Inline, the content IS BODY[1], which is exactly
        // the part Apple always fetches to display. Binary documents (PDF, office) cannot be a text body and
        // stay attachments — a large PDF may still defer, but "download a PDF" is a normal outcome where "an
        // empty message" is not.
        //
        // The signature stays (#783): it trails the content in the SAME text body rather than living in a
        // sibling, keeping the whole message a single text part — no BODY[2] to be misnumbered (#766) and
        // nothing for Apple to defer.
        //
        // text/PLAIN only, NOT text/* — and that distinction is a shipped regression corrected (#1155). The
        // first cut inlined every text/* type, which swept in the two text types that are STRUCTURED OBJECTS a
        // mail client handles specially rather than text to read: text/calendar (.ics) and text/vcard (.vcf).
        // A flight-log entry is an .ics; inlined as a plain body it arrived as a raw VCALENDAR dumped in the
        // message, with no calendar part for the client to recognise or add — the very .ics-ness that is the
        // point of it, gone. Those keep their typed part (below), where a client sees an event/contact; only
        // genuinely-plain text (a NOTAM briefing, a .txt) becomes the body. text/html would render as source
        // if inlined, so it stays a part too — text/plain is the whole of what "read as the body" means here.
        if (mimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            // UTF-8 with replacement on invalid bytes rather than a throw: a mis-encoded text document should
            // degrade to readable-ish text, never 500 a mailbox listing.
            var documentText = Encoding.UTF8.GetString(content.ToArray());
            mime.Body = new TextPart("plain") { Text = $"{documentText}\n\n{SyntheticText(message)}" };
        }
        else
        {
            mime.Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = SyntheticText(message) },
                // The media type is derived from the EXTENSION rather than left to default. A MimePart with no
                // content type is application/octet-stream, so every attachment this server synthesised — a PDF,
                // a JPEG, a Word document — arrived as an anonymous blob: a client cannot preview it, cannot pick
                // an icon for it, and cannot offer "open with". The bytes were always right; what was missing was
                // the one header that says what they are.
                new MimePart(mimeType)
                {
                    Content = new MimeContent(new MemoryStream(content.ToArray())),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = message.Name + message.Extension },
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName = message.Name + message.Extension,
                },
            };
        }

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

    /// <summary>The synthetic body: what the clients' detail pane shows, then the archive signature.</summary>
    /// <remarks>
    /// <para>
    /// It goes in the EXISTING text part rather than an HTML sibling, because a multipart/alternative sibling
    /// moves the attachment out of <c>BODY[2]</c> — the section-number defect class #766 was, and the reason
    /// the bare URL below is a bare URL rather than a link.
    /// </para>
    /// <para>
    /// FETCH and SEARCH share these bytes on purpose (see <c>MessageBytesAsync</c>), so everything written here
    /// is also what a mail client's search matches — an author or an index value becomes findable, which is the
    /// larger half of what this is for.
    /// </para>
    /// </remarks>
    private static string SyntheticText(ImapMessageEntry message)
    {
        var text = new StringBuilder();

        if (message.Details is { } d)
        {
            // No separate title line any more: Name and File extension are ROWS now, and printing the name
            // twice was the old list's way of saying what the pane says in its first row (#1301).
            foreach (var (label, value) in SystemRows(message))
            {
                text.Append(label.PadRight(16)).Append(value).Append('\n');
            }

            if (d.IndexFields.Count > 0)
            {
                text.Append('\n');
                if (d.MaskName is { Length: > 0 } mask)
                {
                    text.Append(mask).Append('\n');
                }

                foreach (var field in d.IndexFields)
                {
                    text.Append(field.Name.PadRight(16)).Append(field.Value).Append('\n');
                }
            }
        }

        // Trailing blank line, and it is two separate fixes wearing one edit. A text body that ends without a
        // line break is malformed-ish RFC 5322 and left our last line unterminated; and a client that renders
        // the attachment INLINE under the text — Apple Mail does — then draws the document's preview flush
        // against the signature URL, so the archive's own footer reads as a caption for the document.
        text.Append("\nServed from the SimplArchive archive:\nhttps://www.simplarchive.dev\n\n");
        return text.ToString();
    }

    // One place both the body rows and the X- headers are derived from, so the two cannot come to disagree
    // about what a document's metadata is.
    /// <summary>
    /// The system rows, in the ONE order both this message and the index-data pane present them
    /// (<see cref="SimplArchive.Presentation.DocumentDetailRows"/>).
    /// </summary>
    /// <remarks>
    /// <b>Standing principle: IMAP shows exactly the information from the details pane.</b> This used to be its
    /// own list — it omitted the name, the file extension, the workflow status, the OCR status and the
    /// retention, ordered what remained differently, and carried a Size row the pane did not have. A reader
    /// comparing the workbench with a mail client got two overlapping answers to one question, and the mail
    /// client is exactly where nobody looks often enough to notice.
    ///
    /// Labels resolve from the SAME resource keys the pane uses, in the INVARIANT culture: a mail message has
    /// no reader's language attached to it, and this is the English the message has always shown. A row with no
    /// value is omitted rather than shown empty, which is what the pane does — it renders no row at all for a
    /// document with no workflow, no retention, no OCR.
    /// </remarks>
    private static List<(string Label, string Value)> SystemRows(ImapMessageEntry message)
    {
        var rows = new List<(string, string)>();
        if (message.Details is not { } d)
        {
            return rows;
        }

        foreach (var row in SimplArchive.Presentation.DocumentDetailRows.InOrder)
        {
            if (ValueOf(row, message, d) is { Length: > 0 } value)
            {
                rows.Add((Localization.Strings.Get(SimplArchive.Presentation.DocumentDetailRows.LabelKey(row), CultureInfo.InvariantCulture), value));
            }
        }

        return rows;
    }

    /// <summary>One row's value, worded as the pane words it.</summary>
    private static string? ValueOf(SimplArchive.Presentation.DocumentDetailRow row, ImapMessageEntry m, ImapMessageDetails d) => row switch
    {
        SimplArchive.Presentation.DocumentDetailRow.Name => m.Name,
        SimplArchive.Presentation.DocumentDetailRow.FileExtension => m.Extension,
        SimplArchive.Presentation.DocumentDetailRow.WorkflowStatus => d.WorkflowStatus,
        // The SAME formatter the pane uses, so the pair renders identically — including the marker that says
        // which zone the time is in. UTC here because a message has no viewer whose zone we could adopt; the
        // pane converts to the reader's. That is a rendering difference the principle allows, and the VALUE is
        // the same instant either way.
        SimplArchive.Presentation.DocumentDetailRow.DocumentDate =>
            SimplArchive.Presentation.DocumentDateFormat.Display(d.DocumentDate, d.DocumentTime, TimeZoneInfo.Utc),
        SimplArchive.Presentation.DocumentDetailRow.OcrLanguages => d.OcrLanguages,
        SimplArchive.Presentation.DocumentDetailRow.OcrStatus => d.OcrStatus,
        SimplArchive.Presentation.DocumentDetailRow.Created => d.Filed.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture),
        SimplArchive.Presentation.DocumentDetailRow.CreatedBy => d.CreatedBy,
        SimplArchive.Presentation.DocumentDetailRow.CurrentVersion => d.VersionNumber is { } n
            ? (d.VersionCount > 0 ? $"{n} of {d.VersionCount}" : n.ToString(CultureInfo.InvariantCulture))
            : null,
        SimplArchive.Presentation.DocumentDetailRow.Size => d.SizeBytes is { } size
            ? SimplArchive.Presentation.HumanFileSize.Format(size)
            : null,
        SimplArchive.Presentation.DocumentDetailRow.Retention => d.Retention,
        _ => null,
    };

    private static IEnumerable<(string Name, string Value)> DetailHeaders(ImapMessageEntry message)
    {
        if (message.Details is not { } d)
        {
            yield break;
        }

        // Header names come from the ROW, not from the label. A label is translated and may contain spaces; a
        // header name must be neither. Deriving it from the label worked only because the labels happened to
        // be English, which is precisely the kind of coincidence that breaks the day someone localises.
        foreach (var row in SimplArchive.Presentation.DocumentDetailRows.InOrder)
        {
            if (ValueOf(row, message, d) is { Length: > 0 } value)
            {
                yield return (SimplArchive.Presentation.DocumentDetailRows.HeaderName(row), Flatten(value));
            }
        }

        if (d.MaskName is { Length: > 0 } mask)
        {
            yield return ("X-SimplArchive-Mask", Flatten(mask));
        }

        foreach (var field in d.IndexFields)
        {
            yield return ($"X-SimplArchive-Field-{HeaderToken(field.Name)}", Flatten(field.Value));
        }
    }

    // A header value is one line. A multi-line index value (a notes field) would otherwise be folded into
    // something a client reads as a new header, so newlines collapse to spaces before it is ever written.
    private static string Flatten(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
             .Replace('\n', ' ')
             .Replace('\r', ' ')
             .Trim();

    // A field name is user-authored and may hold spaces or punctuation that is not legal in a header NAME.
    private static string HeaderToken(string name)
    {
        var token = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            token.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var cleaned = token.ToString().Trim('-');
        return cleaned.Length == 0 ? "Field" : cleaned;
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

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

    /// <summary>
    /// The <c>BODY</c> / <c>BODYSTRUCTURE</c> response for one entity (RFC 3501 §7.4.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="extended"/> is what tells the two apart</b>, and it used to be accepted and then
    /// ignored — both forms returned the non-extensible <c>BODY</c> shape. The extension data it withholds is
    /// not decoration: its second field is the <b>body disposition</b>, which is where
    /// <c>("attachment" ("FILENAME" …))</c> lives, and that is how a client decides a part is an ATTACHMENT
    /// rather than content to render.
    /// </para>
    /// <para>
    /// Apple Mail asks <c>BODYSTRUCTURE</c>, saw no disposition, and rendered the base64 of a PDF as the
    /// message text. The message itself was well-formed the whole time — correct boundaries, correct blank
    /// lines, a proper <c>Content-Disposition</c> header in the part — so anything that read the MESSAGE was
    /// satisfied and only a client that trusts BODYSTRUCTURE was wrong. MailKit fills missing extension data
    /// with NIL and carried on, which is why our own tests never saw it: a tolerant client hides a wire defect.
    /// </para>
    /// </remarks>
    private static string BodyStructure(MimeEntity? entity, bool extended)
    {
        switch (entity)
        {
            case Multipart multipart:
                {
                    var children = string.Concat(multipart.Select(c => BodyStructure(c, extended)));
                    var subtype = Quote(multipart.ContentType.MediaSubtype.ToUpperInvariant());

                    // A multipart's extension data is ordered differently from a part's: parameters first,
                    // then disposition, language, location.
                    var tail = extended
                        ? $" {Parameters(multipart.ContentType)} {Disposition(multipart.ContentDisposition)} NIL NIL"
                        : string.Empty;
                    return $"({children} {subtype}{tail})";
                }
            case MessagePart:
                // A message/rfc822 part serves as an opaque leaf in this slice.
                return "(\"MESSAGE\" \"RFC822\" NIL NIL NIL \"7BIT\" 0)";
            case MimePart part:
                {
                    var encoding = part.ContentTransferEncoding switch
                    {
                        ContentEncoding.Base64 => "BASE64",
                        ContentEncoding.QuotedPrintable => "QUOTED-PRINTABLE",
                        ContentEncoding.EightBit => "8BIT",
                        _ => "7BIT",
                    };
                    var size = part.Content?.Stream?.Length ?? 0;

                    // body-fld-lines. RFC 3501 defines it as "the size of the body in text lines" — a COUNT,
                    // not an approximation, and it is COUNTED here (#1141). It used to be `size / 60`, and the
                    // variable was honestly called lineEstimate: for the synthetic detail body that guessed 3
                    // where the truth was 10. Every other field in the response was correct, which is what made
                    // it worth finding — it was the one thing on the wire that was not true.
                    //
                    // It survived because MailKit does not care, so no test through a client library could
                    // see it, and the E2E tests that DO read the raw wire assert with Contains — a substring
                    // cannot notice that a number is wrong. A strict client reading a short line count can
                    // decide the part ends before it does.
                    var lines = part.ContentType.IsMimeType("text", "*") ? $" {CountLines(part.Content)}" : string.Empty;

                    // MD5, disposition, language, location — in that order (RFC 3501). Only the disposition is
                    // answered with anything; the other three are honestly NIL rather than omitted, because a
                    // client counts fields positionally and a short list is not the same as a list of nulls.
                    var tail = extended
                        ? $" NIL {Disposition(part.ContentDisposition)} NIL NIL"
                        : string.Empty;

                    return $"({Quote(part.ContentType.MediaType.ToUpperInvariant())} {Quote(part.ContentType.MediaSubtype.ToUpperInvariant())} "
                        + $"{Parameters(part.ContentType)} NIL NIL {Quote(encoding)} {size}{lines}{tail})";
                }
            default:
                return "(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"US-ASCII\") NIL NIL \"7BIT\" 0 0)";
        }
    }

    /// <summary>A content type's parameters as the parenthesized list BODYSTRUCTURE wants, or NIL.</summary>
    /// <summary>
    /// The number of text lines in a part's content, as stored — the encoding is not undone, because
    /// <c>body-fld-octets</c> beside it counts encoded octets too and the pair must describe the same bytes.
    /// </summary>
    /// <remarks>
    /// A final line with no terminator still counts: "a\r\nb" is two lines, not one. Reading the stream is
    /// affordable here because the same stream is about to be written to the client anyway, and the position
    /// is restored so this stays an observation rather than a consumption.
    /// </remarks>
    private static int CountLines(IMimeContent? content)
    {
        if (content?.Stream is not { } stream)
        {
            return 0;
        }

        var restore = stream.CanSeek ? stream.Position : 0L;
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var lines = 0;
        var any = false;
        var endedWithNewline = true;
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            any = true;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    lines++;
                    endedWithNewline = true;
                }
                else
                {
                    endedWithNewline = false;
                }
            }
        }

        if (stream.CanSeek)
        {
            stream.Position = restore;
        }

        return any && !endedWithNewline ? lines + 1 : lines;
    }

    private static string Parameters(ContentType contentType) =>
        contentType.Parameters.Count == 0
            ? "NIL"
            : "(" + string.Join(' ', contentType.Parameters.Select(p => $"{Quote(p.Name.ToUpperInvariant())} {Quote(p.Value)}")) + ")";

    /// <summary>
    /// The body-disposition field: <c>("ATTACHMENT" ("FILENAME" "invoice.pdf"))</c>, or NIL when the part
    /// declares none.
    /// </summary>
    /// <remarks>
    /// The field this whole method exists for. A part with no disposition is content a client renders; a part
    /// disposed as an attachment is a file it offers to save, and nothing else in the response says which.
    /// </remarks>
    private static string Disposition(ContentDisposition? disposition)
    {
        if (disposition is null)
        {
            return "NIL";
        }

        var parameters = disposition.Parameters.Count == 0
            ? "NIL"
            : "(" + string.Join(' ', disposition.Parameters.Select(p => $"{Quote(p.Name.ToUpperInvariant())} {Quote(p.Value)}")) + ")";
        return $"({Quote(disposition.Disposition.ToUpperInvariant())} {parameters})";
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
