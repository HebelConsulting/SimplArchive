namespace SimplArchive.Domain.Users;

/// <summary>
/// What a user's personal space is called: their display name, made safe to be both a document name and a
/// WebDAV path segment (ADR 0671).
/// </summary>
/// <remarks>
/// <para>
/// Sanitised INTO THE NAME rather than only into the path, which is the whole point: the mounted drive and the
/// Repositories tree are one surface (ADR 0509), so a name that had to be cleaned up on its way into a URL
/// would make the two disagree about what the same node is called. Cleaning it once, here, means the document
/// name and the path segment are the same string by construction rather than by two rules that must agree.
/// </para>
/// <para>
/// This is NOT general-purpose escaping. <see cref="Uri.EscapeDataString"/> already handles everything a URL
/// needs when an href is built; what it cannot fix is a name containing a separator, because the path is split
/// on <c>/</c> before any unescaping happens — one display name would arrive as two segments and address
/// something that does not exist.
/// </para>
/// </remarks>
public static class PersonalSpaceName
{
    /// <summary>The name for this user's personal space.</summary>
    public static string For(User user) => For(user.DisplayName, user.Email);

    /// <inheritdoc cref="For(User)"/>
    public static string For(string? displayName, string email)
    {
        var sanitised = Sanitise(displayName);

        // An empty display name is not hypothetical — it is whitespace away, and a personal space named "" would
        // be a root nobody can address at all. The email's local part is the one other thing every user has that
        // reads as a person rather than as an id.
        return sanitised.Length > 0 ? sanitised : Sanitise(LocalPart(email)) is { Length: > 0 } fallback
            ? fallback
            : "Personal";
    }

    private static string Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Path separators and control characters become a hyphen — replaced rather than dropped, so "Anna/Tom"
        // stays two readable words instead of collapsing into one. Everything else a display name might contain
        // survives untouched: accents, apostrophes, spaces and non-Latin scripts are all legal in both a
        // document name and a path segment.
        var cleaned = new string([.. value.Select(c => c is '/' or '\\' || char.IsControl(c) ? '-' : c)]);

        // Collapsed and trimmed last, so the replacement above cannot leave a trailing hyphen-space run — and
        // because a name with a leading or trailing space is one that looks identical to another and is not.
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string LocalPart(string email) =>
        email.Split('@') is [var local, ..] ? local : email;
}
