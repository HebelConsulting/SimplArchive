namespace SimplArchive.Api.Imap;

// STORE / UID STORE (#562 slice 2, ADR "IMAP endpoint: persisted read state"): \Seen is the one storable
// flag — added, removed or replaced per message, persisted per user + document. Other flags in the request
// are politely ignored (PERMANENTFLAGS advertises only \Seen, so a conforming client won't send them; a
// non-conforming one gets its \Seen handled and nothing else stored).
internal static class ImapStore
{
    internal static async Task StoreAsync(
        ImapSession session, IServiceScope scope, string tag, ImapSelectedMailbox selected, string arguments, bool uidMode)
    {
        // <set> <±FLAGS[.SILENT]> (<flags>)
        var tokens = ImapProtocol.Tokenize(arguments);
        if (tokens.Count < 3)
        {
            await session.WriteLineAsync($"{tag} BAD STORE expects a set, an operation and flags");
            return;
        }

        var set = tokens[0];
        var operation = tokens[1].ToUpperInvariant();
        var silent = operation.EndsWith(".SILENT", StringComparison.Ordinal);
        var baseOperation = silent ? operation[..^".SILENT".Length] : operation;
        var wantsSeen = tokens[2].Contains("\\Seen", StringComparison.OrdinalIgnoreCase);

        bool? targetSeen = baseOperation switch
        {
            "+FLAGS" => wantsSeen ? true : null,
            "-FLAGS" => wantsSeen ? false : null,
            // A replace sets exactly the listed flags: \Seen present → seen, absent → unseen.
            "FLAGS" => wantsSeen,
            _ => null,
        };

        if (baseOperation is not ("+FLAGS" or "-FLAGS" or "FLAGS"))
        {
            await session.WriteLineAsync($"{tag} BAD unknown STORE operation");
            return;
        }

        var lastUid = selected.Messages.Count == 0 ? 0 : selected.Messages[^1].Uid;
        for (var index = 0; index < selected.Messages.Count; index++)
        {
            var message = selected.Messages[index];
            var sequence = index + 1;
            if (!ImapFetch.InSet(set, uidMode ? message.Uid : sequence, uidMode ? lastUid : selected.Messages.Count))
            {
                continue;
            }

            if (targetSeen is { } seen)
            {
                await ImapMailboxes.MarkSeenAsync(scope, message.DocumentId, seen);
            }

            if (!silent)
            {
                var seenNow = targetSeen ?? (await ImapMailboxes.SeenSetAsync(scope, [message])).Contains(message.DocumentId);
                var uidPart = uidMode ? $"UID {message.Uid} " : string.Empty;
                await session.WriteLineAsync($"* {sequence} FETCH ({uidPart}FLAGS ({(seenNow ? "\\Seen" : string.Empty)}))");
            }
        }

        await session.OkAsync(tag, uidMode ? "UID STORE" : "STORE");
    }
}
