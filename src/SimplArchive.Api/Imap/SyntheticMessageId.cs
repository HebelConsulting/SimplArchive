namespace SimplArchive.Api.Imap;

/// <summary>
/// The self-identifying Message-ID SimplArchive mints when it serves a NON-<c>.eml</c> document as a synthetic
/// mail over IMAP (<see cref="ImapFetch"/>): the value <c>{documentId}@simplarchive</c>. It embeds the wrapped
/// document's own id, so a re-filed export of that synthetic mail can be recognised as ours and offered as a
/// REFERENCE to the original rather than growing a silent, mail-shaped duplicate (#782).
/// </summary>
/// <remarks>
/// Minted and parsed in ONE place so the two forms cannot drift — the trap the duplicate probe already learned
/// with <c>NormalizeMessageId</c> (#704). The parsed id is a HINT from a header that travels outside the system
/// in a file anyone can edit: the caller MUST still tenant-scope and rights-check it (#782's trap), never treat
/// it as authorisation.
/// </remarks>
internal static class SyntheticMessageId
{
    private const string Suffix = "@simplarchive";

    /// <summary>The Message-ID value (no angle brackets) for a document served synthetically.</summary>
    internal static string For(Guid documentId) => $"{documentId}{Suffix}";

    /// <summary>
    /// The document id a Message-ID names IF it is one of ours, else null. Accepts the bracketed
    /// (<c>&lt;{guid}@simplarchive&gt;</c>) and bare forms — the stored/normalised Entry ID is bracketed.
    /// </summary>
    internal static Guid? DocumentIdOf(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        var value = messageId.Trim().TrimStart('<').TrimEnd('>').Trim();
        if (!value.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Guid.TryParse(value[..^Suffix.Length], out var id) ? id : null;
    }
}
