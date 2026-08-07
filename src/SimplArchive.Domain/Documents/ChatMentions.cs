using System.Text.RegularExpressions;

namespace SimplArchive.Domain.Documents;

/// <summary>
/// The stored form of an @-mention (issue #383) — defined once, here, because a body written by one client is
/// read by the other, by the export archive, and by the external-system interop layer.
///
/// A mention is stored as <c>@[{userId}]</c> and NEVER as the typed display name. Display names contain spaces,
/// so <c>@Demo Admin</c> has no delimiter; they are not unique, so a name cannot identify a person; and they can
/// be renamed, so any stored copy goes stale. The id is the only stable thing to keep, and the name is resolved
/// at render time from that id.
/// </summary>
public static partial class ChatMentions
{
    /// <summary>Renders the token a client stores for a mention of <paramref name="userId"/>.</summary>
    public static string Token(Guid userId) => $"@[{userId}]";

    /// <summary>
    /// The distinct user ids a body addresses, in order of first appearance. Order matters only so a message
    /// mentioning several people notifies them in the order they were written into it.
    /// </summary>
    public static IReadOnlyList<Guid> Parse(string body)
    {
        var found = new List<Guid>();
        foreach (Match match in TokenPattern().Matches(body))
        {
            if (Guid.TryParse(match.Groups[1].Value, out var userId) && !found.Contains(userId))
            {
                found.Add(userId);
            }
        }

        return found;
    }

    /// <summary>
    /// Replaces every token with <c>@{name}</c> using <paramref name="nameFor"/>, for readers that cannot resolve
    /// ids themselves — an export to a system with no mention concept, and any plain-text rendering. A token whose
    /// user cannot be resolved gets <paramref name="fallbackName"/>, so the sentence stays readable rather than
    /// showing a raw id or silently losing the fact that somebody was addressed.
    /// </summary>
    public static string Flatten(string body, Func<Guid, string?> nameFor, string fallbackName) =>
        TokenPattern().Replace(body, match =>
            Guid.TryParse(match.Groups[1].Value, out var userId)
                ? $"@{nameFor(userId) ?? fallbackName}"
                : match.Value);

    /// <summary>
    /// Rewrites every token through <paramref name="mapUser"/>, for an import that recreates users under fresh
    /// ids in the destination tenant. A token whose user does not map is left ALONE rather than dropped: the id
    /// no longer resolves, so it renders as the unknown-user tombstone, which preserves the fact that somebody
    /// was addressed. Silently deleting it would rewrite what the author wrote.
    /// </summary>
    public static string Remap(string body, Func<Guid, Guid?> mapUser) =>
        TokenPattern().Replace(body, match =>
            Guid.TryParse(match.Groups[1].Value, out var userId) && mapUser(userId) is { } mapped
                ? Token(mapped)
                : match.Value);

    // Deliberately strict: the 8-4-4-4-12 hyphenated form only. A looser pattern would let ordinary prose
    // containing "@[...]" turn into a mention, and this runs over every message body that is ever rendered.
    [GeneratedRegex(@"@\[([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]")]
    private static partial Regex TokenPattern();
}
