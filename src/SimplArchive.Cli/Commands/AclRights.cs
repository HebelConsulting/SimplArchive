using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// The vocabulary <c>--rights</c> speaks: the nine rights, the presets that expand to common sets, and the
/// subtraction that makes a preset usable when it is nearly right (#1504).
/// </summary>
/// <remarks>
/// <para>
/// <b>A list rather than nine flags</b> (owner, 2026-10-01). Nine booleans on a command line makes
/// "everything" nine options long, and the ACL model allows 2^9 sets — so presets alone would reach four of
/// them. The list keeps the exact cases expressible and the common ones short.
/// </para>
/// <para>
/// <b>An unrecognised name is refused and the refusal LISTS the vocabulary.</b> A right is not guessable from
/// the domain — <c>edit-index-data</c> and <c>edit-content</c> are different rights over the same document —
/// so a bare "unknown right" would send the operator to the source. Refusing with the set is the difference
/// between a typo costing a second and costing a search.
/// </para>
/// <para>
/// <b>Pure, and tested as such.</b> Nothing here touches the network, so every decision in it is pinned by a
/// unit test rather than by an end-to-end run that would exercise one combination and call it covered.
/// </para>
/// </remarks>
public sealed record AclRights(
    bool CanSee,
    bool CanReadContent,
    bool CanEditContent,
    bool CanEditIndexData,
    bool CanDelete,
    bool CanCreateSubItems,
    bool CanManagePermissions,
    bool CanMove,
    bool CanAnnotate)
{
    /// <summary>Nothing granted — what a <c>set</c> naming no rights means, and the base a preset builds on.</summary>
    public static AclRights None { get; } = new(false, false, false, false, false, false, false, false, false);

    /// <summary>The nine, in the order the ACL resource presents them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "see", "read-content", "edit-content", "edit-index-data", "delete",
        "create-sub-items", "manage-permissions", "move", "annotate",
    ];

    /// <summary>The presets, expanded. Deliberately few, and each one a sentence somebody would actually say.</summary>
    /// <remarks>
    /// <c>manage</c> is <b>not</b> "everything": it adds the permission right to a READER rather than to an
    /// editor, because letting somebody hand out rights is a different decision from letting them change
    /// content. Bundling the two is how a preset grants more than was meant, and the escalation cap would not
    /// catch it — a caller who holds both may grant both.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Presets { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["read"] = ["see", "read-content"],
            ["write"] = ["see", "read-content", "edit-content", "edit-index-data", "create-sub-items", "annotate"],
            ["manage"] = ["see", "read-content", "manage-permissions"],
            ["full"] = All,
        };

    /// <summary>Whether this right is granted, by its command-line name.</summary>
    public bool Has(string name) => name switch
    {
        "see" => CanSee,
        "read-content" => CanReadContent,
        "edit-content" => CanEditContent,
        "edit-index-data" => CanEditIndexData,
        "delete" => CanDelete,
        "create-sub-items" => CanCreateSubItems,
        "manage-permissions" => CanManagePermissions,
        "move" => CanMove,
        "annotate" => CanAnnotate,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a right."),
    };

    /// <summary>This set with <paramref name="name"/> granted or withdrawn.</summary>
    public AclRights With(string name, bool granted) => name switch
    {
        "see" => this with { CanSee = granted },
        "read-content" => this with { CanReadContent = granted },
        "edit-content" => this with { CanEditContent = granted },
        "edit-index-data" => this with { CanEditIndexData = granted },
        "delete" => this with { CanDelete = granted },
        "create-sub-items" => this with { CanCreateSubItems = granted },
        "manage-permissions" => this with { CanManagePermissions = granted },
        "move" => this with { CanMove = granted },
        "annotate" => this with { CanAnnotate = granted },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a right."),
    };

    /// <summary>The granted rights, by name, in the canonical order.</summary>
    public IReadOnlyList<string> Granted => [.. All.Where(Has)];

    /// <summary>
    /// Expands a <c>--rights</c> expression into the named rights — presets, exact names, and <c>-name</c> to
    /// take one back out.
    /// </summary>
    /// <remarks>
    /// <b>Order matters and is left to right</b>, so <c>write,-edit-content</c> is "the write preset, without
    /// that one" while <c>-edit-content,write</c> is just the write preset. That is the ordinary reading of a
    /// list and the only one that makes subtraction useful; the alternative — subtractions applied last,
    /// wherever they appear — is identical in the common case and differs exactly when somebody has written
    /// something unusual on purpose.
    /// </remarks>
    public static IReadOnlyList<string> Expand(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [];
        }

        var chosen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var remove = raw.StartsWith('-');
            var token = remove ? raw[1..].Trim() : raw;

            IReadOnlyList<string> names =
                Presets.TryGetValue(token, out var preset) ? preset
                : All.FirstOrDefault(r => string.Equals(r, token, StringComparison.OrdinalIgnoreCase)) is { } exact
                    ? [exact]
                    : throw new CliException(Unknown(token));

            foreach (var name in names)
            {
                if (remove)
                {
                    chosen.Remove(name);
                }
                else
                {
                    chosen.Add(name);
                }
            }
        }

        return [.. All.Where(chosen.Contains)];
    }

    /// <summary>Parses a <c>--rights</c> expression into a complete set, every unnamed right withheld.</summary>
    public static AclRights Parse(string expression) =>
        Expand(expression).Aggregate(None, (rights, name) => rights.With(name, granted: true));

    /// <summary>This set plus the named rights.</summary>
    public AclRights Plus(IEnumerable<string> names) =>
        names.Aggregate(this, (rights, name) => rights.With(name, granted: true));

    /// <summary>This set minus the named rights.</summary>
    public AclRights Minus(IEnumerable<string> names) =>
        names.Aggregate(this, (rights, name) => rights.With(name, granted: false));

    /// <summary>What an operator is told when they name something that is not a right.</summary>
    internal static string Unknown(string token) =>
        $"'{token}' is not a right or a preset. Rights: {string.Join(", ", All)}. "
        + $"Presets: {string.Join(", ", Presets.Keys)}. Prefix a name with '-' to take it back out, "
        + "e.g. --rights write,-edit-content.";
}
