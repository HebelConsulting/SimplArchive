using System.Globalization;

namespace SimplArchive.Api.Imap;

/// <summary>
/// SEARCH and UID SEARCH (RFC 3501 §6.4.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> It was refused — <c>NO not supported in this slice</c> — while the greeting
/// advertised <c>IMAP4rev1</c>, in which SEARCH is <b>mandatory</b>. A client is entitled to rely on that, and
/// one did: a mail client that enumerates a mailbox with <c>UID SEARCH</c> concluded there were no messages and
/// showed <b>every folder empty</b>, while another client on the same account worked perfectly because it
/// enumerates with FETCH. Nothing in the logs distinguished the two (now they do — ADR 0626). Advertising a
/// protocol level is a promise about its mandatory commands, not an aspiration.
/// </para>
/// <para>
/// <b>What it searches.</b> A mailbox here is a folder and a message is a document, so the criteria split in
/// two. The <i>metadata</i> ones — sets, flags, dates, sizes — are answered from the already-loaded message
/// list and cost nothing. The <i>content</i> ones (SUBJECT, FROM, BODY, TEXT, HEADER) need the message itself,
/// so they fetch bytes, and only for the candidates still standing after the cheap criteria have run. A search
/// with no content criterion never touches object storage.
/// </para>
/// </remarks>
internal static class ImapSearch
{
    internal static async Task SearchAsync(
        ImapSession session, IServiceScope scope, string tag, ImapSelectedMailbox selected, string arguments, bool uidMode)
    {
        var tokens = ImapProtocol.Tokenize(arguments);

        // CHARSET <name> may precede the criteria (RFC 3501 §6.4.4). We read UTF-8 and US-ASCII alike, so the
        // announcement is accepted and skipped rather than refused — a NO here would fail the whole search for
        // a client that was merely being explicit.
        if (tokens.Count >= 2 && tokens[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
        {
            tokens = tokens.Skip(2).ToList();
        }

        // A bare SEARCH with no criteria is malformed; RFC 3501 requires at least one key.
        if (tokens.Count == 0)
        {
            await session.WriteLineAsync($"{tag} BAD SEARCH expects at least one criterion");
            return;
        }

        var seen = await ImapMailboxes.SeenSetAsync(scope, selected.Messages);
        var parser = new CriteriaParser(tokens, selected, seen);

        Predicate criteria;
        try
        {
            criteria = parser.ParseAll();
        }
        catch (UnsupportedCriterionException e)
        {
            // Loud, and it names the switch — the whole point of ADR 0626. An unsupported KEY is far less
            // damaging than the old blanket refusal (the client at least knows this search failed), but it is
            // still a silent-wrong-answer risk if we guessed instead.
            await session.RefuseSearchAsync(tag, e.Criterion);
            return;
        }

        // Cheap criteria first, so the content pass — the only one that reads object storage — runs over the
        // survivors rather than the mailbox.
        var candidates = new List<(ImapMessageEntry Message, int Sequence)>();
        for (var i = 0; i < selected.Messages.Count; i++)
        {
            var message = selected.Messages[i];
            var sequence = i + 1;
            if (criteria.MatchesMetadata(message, sequence))
            {
                candidates.Add((message, sequence));
            }
        }

        var matches = new List<int>();
        foreach (var (message, sequence) in candidates)
        {
            if (criteria.NeedsContent && !await criteria.MatchesContentAsync(message, scope))
            {
                continue;
            }

            matches.Add(uidMode ? message.Uid : sequence);
        }

        // The untagged response carries the numbers even when there are none — "* SEARCH" with an empty list is
        // the correct answer for "nothing matched", and is NOT the same as refusing the command.
        await session.WriteLineAsync(matches.Count == 0 ? "* SEARCH" : $"* SEARCH {string.Join(' ', matches)}");
        await session.OkAsync(tag, uidMode ? "UID SEARCH" : "SEARCH");
    }

    /// <summary>A criterion we do not implement — carried out to the caller so the refusal can name it.</summary>
    private sealed class UnsupportedCriterionException(string criterion) : Exception($"unsupported search key {criterion}")
    {
        public string Criterion { get; } = criterion;
    }

    /// <summary>One parsed search key: a metadata test, a content test, or a combination of them.</summary>
    internal sealed class Predicate
    {
        private readonly List<Func<ImapMessageEntry, int, bool>> _metadata = [];
        private readonly List<Func<string, bool>> _content = [];
        private readonly List<(Predicate Left, Predicate Right)> _alternatives = [];
        private readonly List<Predicate> _negated = [];

        public bool NeedsContent =>
            _content.Count > 0
            || _negated.Any(n => n.NeedsContent)
            || _alternatives.Any(a => a.Left.NeedsContent || a.Right.NeedsContent);

        internal void AddMetadata(Func<ImapMessageEntry, int, bool> test) => _metadata.Add(test);

        internal void AddContent(Func<string, bool> test) => _content.Add(test);

        internal void AddAlternative(Predicate left, Predicate right) => _alternatives.Add((left, right));

        internal void AddNegated(Predicate inner) => _negated.Add(inner);

        /// <summary>
        /// The cheap half. An OR or NOT whose branches need CONTENT cannot be decided here, so it passes —
        /// deliberately over-inclusive, because the content pass runs afterwards and can only narrow. Answering
        /// "no" here on a criterion we have not evaluated yet is how a search silently loses messages.
        /// </summary>
        internal bool MatchesMetadata(ImapMessageEntry message, int sequence)
        {
            if (!_metadata.All(test => test(message, sequence)))
            {
                return false;
            }

            foreach (var negated in _negated.Where(n => !n.NeedsContent))
            {
                if (negated.MatchesMetadata(message, sequence))
                {
                    return false;
                }
            }

            foreach (var (left, right) in _alternatives.Where(a => !a.Left.NeedsContent && !a.Right.NeedsContent))
            {
                if (!left.MatchesMetadata(message, sequence) && !right.MatchesMetadata(message, sequence))
                {
                    return false;
                }
            }

            return true;
        }

        internal async Task<bool> MatchesContentAsync(ImapMessageEntry message, IServiceScope scope)
        {
            // The SAME bytes FETCH would return — via ImapFetch, not a second reader. A search matching on
            // different bytes than the fetch serves would produce hits the user cannot then find.
            //
            // Latin-1 decodes any byte sequence without throwing and preserves the octets one-for-one, which is
            // what a substring search over a MIME message needs; a UTF-8 decode would mangle an 8-bit body and
            // could silently drop a match.
            var storage = scope.ServiceProvider.GetRequiredService<Application.Abstractions.IObjectStorageClient>();
            var bytes = await ImapFetch.MessageBytesAsync(storage, message);
            return Evaluate(System.Text.Encoding.Latin1.GetString(bytes), message);
        }

        private bool Evaluate(string text, ImapMessageEntry message)
        {
            if (!_content.All(test => test(text)))
            {
                return false;
            }

            foreach (var negated in _negated.Where(n => n.NeedsContent))
            {
                if (negated.Evaluate(text, message))
                {
                    return false;
                }
            }

            foreach (var (left, right) in _alternatives.Where(a => a.Left.NeedsContent || a.Right.NeedsContent))
            {
                if (!left.Evaluate(text, message) && !right.Evaluate(text, message))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Turns the token stream into one ANDed <see cref="Predicate"/>, as RFC 3501 defines it.</summary>
    private sealed class CriteriaParser(List<string> tokens, ImapSelectedMailbox selected, HashSet<Guid> seen)
    {
        private int _position;

        internal Predicate ParseAll()
        {
            var predicate = new Predicate();
            while (_position < tokens.Count)
            {
                ParseOne(predicate);
            }

            return predicate;
        }

        private string Next() =>
            _position < tokens.Count ? tokens[_position++] : throw new UnsupportedCriterionException("(truncated)");

        private void ParseOne(Predicate into)
        {
            var key = Next();

            // A bare sequence set is a valid key ("1:5", "*", "2,4:7") — and it is what many clients send after
            // an ALL-less SEARCH. Recognised by shape, since it carries no keyword.
            if (key.Length > 0 && (char.IsAsciiDigit(key[0]) || key[0] == '*'))
            {
                into.AddMetadata((_, sequence) => ImapFetch.InSet(key, sequence, selected.Messages.Count));
                return;
            }

            switch (key.ToUpperInvariant())
            {
                case "ALL":
                    return;

                // Flags. \Seen is the only one persisted and \Deleted the only session one (PERMANENTFLAGS
                // advertises exactly that), so the rest answer from what the store CAN know: nothing is
                // answered/flagged/draft here, which makes their negations match everything. Answering the
                // negations as "none" instead would hide every message from a client that filters UNANSWERED.
                case "SEEN":
                    into.AddMetadata((m, _) => seen.Contains(m.DocumentId));
                    return;
                case "UNSEEN":
                    into.AddMetadata((m, _) => !seen.Contains(m.DocumentId));
                    return;
                case "DELETED":
                    into.AddMetadata((m, _) => selected.DeletedDocumentIds.Contains(m.DocumentId));
                    return;
                case "UNDELETED":
                    into.AddMetadata((m, _) => !selected.DeletedDocumentIds.Contains(m.DocumentId));
                    return;
                case "ANSWERED" or "FLAGGED" or "DRAFT" or "RECENT" or "KEYWORD":
                    if (key.Equals("KEYWORD", StringComparison.OrdinalIgnoreCase))
                    {
                        Next(); // the keyword itself; no keywords are stored, so nothing matches
                    }

                    into.AddMetadata((_, _) => false);
                    return;
                case "UNANSWERED" or "UNFLAGGED" or "UNDRAFT" or "OLD" or "UNKEYWORD":
                    if (key.Equals("UNKEYWORD", StringComparison.OrdinalIgnoreCase))
                    {
                        Next();
                    }

                    return; // matches everything — no filter added
                case "NEW":
                    // NEW = RECENT and UNSEEN. Nothing is \Recent here (we never advertise it), so NEW is empty
                    // by construction rather than by an unimplemented branch.
                    into.AddMetadata((_, _) => false);
                    return;

                // Dates, on the message's internal date. SENT* would properly read the Date header; for a
                // synthetic message that header IS the internal date, and for a stored .eml the two agree
                // closely enough that a day-granularity filter cannot tell them apart.
                case "SINCE" or "SENTSINCE":
                    AddDate(into, Next(), (internalDate, value) => internalDate >= value);
                    return;
                case "BEFORE" or "SENTBEFORE":
                    AddDate(into, Next(), (internalDate, value) => internalDate < value);
                    return;
                case "ON" or "SENTON":
                    AddDate(into, Next(), (internalDate, value) => internalDate == value);
                    return;

                case "LARGER":
                    AddSize(into, Next(), (size, value) => size > value);
                    return;
                case "SMALLER":
                    AddSize(into, Next(), (size, value) => size < value);
                    return;

                case "UID":
                    var set = Next();
                    into.AddMetadata((m, _) => ImapFetch.InSet(set, m.Uid, LastUid()));
                    return;

                case "NOT":
                    var negated = new Predicate();
                    ParseOne(negated);
                    into.AddNegated(negated);
                    return;
                case "OR":
                    var left = new Predicate();
                    var right = new Predicate();
                    ParseOne(left);
                    ParseOne(right);
                    into.AddAlternative(left, right);
                    return;

                // Content. HEADER takes a field AND a value; the rest take one value.
                case "HEADER":
                    var field = Next();
                    var headerValue = Next();
                    into.AddContent(text => ContainsHeader(text, field, headerValue));
                    return;
                case "SUBJECT" or "FROM" or "TO" or "CC" or "BCC":
                    var namedField = key.ToUpperInvariant();
                    var fieldValue = Next();
                    into.AddContent(text => ContainsHeader(text, namedField, fieldValue));
                    return;
                case "BODY" or "TEXT":
                    var needle = Next();
                    into.AddContent(text => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
                    return;

                default:
                    throw new UnsupportedCriterionException(key);
            }
        }

        private int LastUid() => selected.Messages.Count == 0 ? 0 : selected.Messages[^1].Uid;

        private static void AddDate(Predicate into, string token, Func<DateOnly, DateOnly, bool> compare)
        {
            // RFC 3501 dates are "d-MMM-yyyy" and compared by DAY, in the server's sense of the date — so the
            // message's timestamp is reduced to a date before comparing, not the other way round.
            if (!DateTime.TryParseExact(
                    token.Trim('"'), "d-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                throw new UnsupportedCriterionException($"date {token}");
            }

            var value = DateOnly.FromDateTime(parsed);
            into.AddMetadata((m, _) => compare(DateOnly.FromDateTime(m.InternalDate.UtcDateTime), value));
        }

        private static void AddSize(Predicate into, string token, Func<long, long, bool> compare)
        {
            if (!long.TryParse(token, out var value))
            {
                throw new UnsupportedCriterionException($"size {token}");
            }

            // A message with no recorded size cannot satisfy either comparison; treating it as 0 would make
            // every SMALLER search return the whole mailbox.
            into.AddMetadata((m, _) => m.SizeBytes is { } size && compare(size, value));
        }

        private static bool ContainsHeader(string text, string field, string value)
        {
            // The headers are everything before the first blank line. Searching the whole message for
            // "Subject: x" would match a quoted reply in the body and report a false hit.
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                end = text.IndexOf("\n\n", StringComparison.Ordinal);
            }

            var headers = end >= 0 ? text[..end] : text;
            var trimmedField = field.Trim('"');

            foreach (var line in headers.Split('\n'))
            {
                if (line.StartsWith($"{trimmedField}:", StringComparison.OrdinalIgnoreCase)
                    && line.Contains(value.Trim('"'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
