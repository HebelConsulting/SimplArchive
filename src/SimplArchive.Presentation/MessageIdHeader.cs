namespace SimplArchive.Presentation;

/// <summary>
/// Extracts an RFC 5322 <c>Message-ID</c> from the head of an e-mail, for duplicate detection (#704).
/// </summary>
/// <remarks>
/// <para>
/// Here — the dependency-free leaf both clients share — because both must answer identically before an
/// upload: the bytes never pass through the Api before the duplicate probe (presigned direct upload), so
/// extraction is client-side, and two per-client copies of a header parser is how the desktop and the web
/// come to disagree about the same file (the ADR 0650/0651 reasoning, applied to a wire fact instead of a
/// calendar cell). The Api's own extraction (MimeKit, at finalize) stays authoritative for what is
/// STORED; this only has to produce the same normalized form for matching, which the round-trip E2E test
/// pins.
/// </para>
/// <para>
/// Deliberately a header SCAN, not a MIME parser: it reads to the first blank line (the end of the header
/// block), unfolds RFC 5322 continuation lines, and matches the header name case-insensitively. A
/// <c>Message-ID:</c> appearing in the body is beyond the blank line and never reached. Callers pass only
/// the head of the file (a few KB) — the whole message is never needed.
/// </para>
/// <para>
/// The normalized form is <c>&lt;inner&gt;</c> with exactly one angle-bracket pair — the same shape the
/// server's <c>EmailMetadataExtractor.NormalizeMessageId</c> stores in the <c>Entry ID</c> field, which is
/// what makes a client-extracted id and a stored one comparable at all.
/// </para>
/// </remarks>
public static class MessageIdHeader
{
    /// <summary>The normalized <c>&lt;id&gt;</c> from a header block, or null when there is none.</summary>
    public static string? Extract(string? headerText)
    {
        if (string.IsNullOrWhiteSpace(headerText))
        {
            return null;
        }

        var lines = headerText.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                return null; // the blank line ends the headers — anything after is body
            }

            if (!line.StartsWith("Message-ID:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Unfold: a header value continues on lines starting with whitespace (RFC 5322 §2.2.3). A folded
            // Message-ID is real — some senders put the id alone on the continuation line.
            var value = line["Message-ID:".Length..];
            while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0]))
            {
                value += lines[++i];
            }

            return Normalize(value);
        }

        return null;
    }

    /// <summary>The overload for a caller holding raw bytes — reads at most the first 8 KB as UTF-8.</summary>
    /// <remarks>Headers are ASCII by RFC 5322; UTF-8 decodes them identically, and a Message-ID beyond 8 KB
    /// of headers is a message this heuristic was never going to save.</remarks>
    public static string? Extract(byte[] bytes) =>
        bytes.Length == 0
            ? null
            : Extract(System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 8192)));

    private static string? Normalize(string raw)
    {
        var inner = raw.Trim().TrimStart('<').TrimEnd('>').Trim();
        return inner.Length == 0 ? null : $"<{inner}>";
    }
}
