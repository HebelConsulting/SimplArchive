using System.ComponentModel;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>What every <c>acl</c> command needs: which document, which principal, which rights (#1504).</summary>
public class AclSettings : TenantSessionSettings
{
    [CommandArgument(0, "<document>")]
    [Description("The document, by id.")]
    public required string Document { get; init; }

    [CommandOption("--user")]
    [Description("The user, by display name.")]
    public string? User { get; init; }

    [CommandOption("--group")]
    [Description("The group, by name.")]
    public string? Group { get; init; }

    [CommandOption("--service-account")]
    [Description("The service account, by name.")]
    public string? ServiceAccount { get; init; }

    [CommandOption("--rights")]
    [Description("Rights, comma-separated. Names, presets (read, write, manage, full), and -name to subtract.")]
    public string Rights { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when new[] { User, Group, ServiceAccount }.Count(p => !string.IsNullOrWhiteSpace(p)) != 1 =>
            ValidationResult.Error("Name exactly one principal: --user, --group or --service-account."),
        _ when !Guid.TryParse(Document, out _) => ValidationResult.Error($"'{Document}' is not a document id."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Resolving a document, a principal and their grant address — by rels, every hop (#1504, ADR 0543).
/// </summary>
/// <remarks>
/// <para>
/// <b>The document is reached through the root's templated <c>document</c> rel</b>, not by composing
/// <c>api/documents/{id}</c>. That distinction is the whole rule: the server owns its URL space, and the one
/// address this tool knows is the API root. Substituting an id into a template the server published is
/// following, not composing.
/// </para>
/// <para>
/// <b>And a grant has no resource until it exists</b>, which is why the address comes from the
/// <c>grantable-principals</c> listing rather than from the entry: each row carries the <c>grant</c> rel —
/// the address at which a grant FOR THAT PRINCIPAL is written. Composing
/// <c>/acl-entries/{type}/{id}</c> from a selection is exactly what that rel exists to make unnecessary.
/// </para>
/// </remarks>
internal static class AclSurface
{
    internal sealed record Target(string Address, string PrincipalKind, string PrincipalName);

    /// <summary>The rel chain: root → document → grantable principals → the row's own grant address.</summary>
    internal static async Task<Target> ResolveAsync(
        SimplArchiveApi api, AclSettings settings, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var template = await hypermedia.RootHrefAsync("document", cancellationToken);
        var document = template.Replace("{id}", settings.Document, StringComparison.Ordinal);

        var documentLinks = await hypermedia.LinksOfAsync(document, cancellationToken);

        // A MISSING REL IS THE ANSWER (ADR 0543). `acl-entries` is advertised only to a caller who may manage
        // permissions, so its absence says "not available to you, here, now" — which is a better answer than
        // letting the PUT return 403 after the operator has typed out a rights expression.
        var entries = documentLinks.TryGetValue("acl-entries", out var acl)
            ? acl
            : throw new CliException(
                $"You may not manage permissions on {settings.Document}, so there is nothing to grant. "
                + "Someone holding 'manage-permissions' on it, or a tenant administrator, can.");

        var principals = Hypermedia.Href(
            await hypermedia.LinksOfAsync(entries, cancellationToken),
            "grantable-principals",
            "The document's access list");

        var (kind, name) = settings switch
        {
            { User: { Length: > 0 } user } => ("users", user),
            { Group: { Length: > 0 } group } => ("groups", group),
            _ => ("service-accounts", settings.ServiceAccount!),
        };

        var listing = await api.GetAsync(principals, cancellationToken);
        var matches = listing.GetProperty("principals").EnumerateArray()
            .Where(p => p.GetProperty("type").GetString() == kind
                && string.Equals(p.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var available = listing.GetProperty("principals").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() == kind)
                .Select(p => p.GetProperty("name").GetString())
                .ToList();

            throw new CliException(
                $"No {kind} called '{name}' can be granted on this document. "
                + (available.Count == 0
                    ? $"The document's picker offers no {kind} at all."
                    : $"It offers: {string.Join(", ", available)}."));
        }

        // AMBIGUITY IS REFUSED RATHER THAN RESOLVED. Users are listed by DISPLAY name, which is not unique —
        // two people called "Anna Meier" is an ordinary thing in an archive, and picking the first would
        // grant rights to whichever row the database happened to order first. The first of N is right by
        // luck until it is not.
        if (matches.Count > 1)
        {
            throw new CliException(
                $"'{name}' matches {matches.Count} {kind} on this document, so it is not clear who is meant. "
                + "Rename one, or grant through the application where the rows are distinguishable.");
        }

        return new Target(
            Hypermedia.Href(Hypermedia.LinksOf(matches[0]), "grant", $"The {kind} row for '{name}'"),
            kind,
            name);
    }

    /// <summary>The rights this principal holds on the document today, and the tag they were read with.</summary>
    /// <remarks>
    /// <b>Absence is an ordinary state here</b>: until somebody is first granted something there is no entry,
    /// so a 404 means "nothing yet" and the tag is null — which is the correct precondition for a create.
    /// </remarks>
    internal static async Task<(AclRights Rights, string? ETag)> CurrentAsync(
        SimplArchiveApi api, string address, CancellationToken cancellationToken)
    {
        if (await api.TryGetWithETagAsync(address, cancellationToken) is not { } existing)
        {
            return (AclRights.None, null);
        }

        var e = existing.Resource;
        bool Flag(string name) => e.TryGetProperty(name, out var value) && value.GetBoolean();

        return (new AclRights(
            Flag("canSee"), Flag("canReadContent"), Flag("canEditContent"), Flag("canEditIndexData"),
            Flag("canDelete"), Flag("canCreateSubItems"), Flag("canManagePermissions"), Flag("canMove"),
            Flag("canAnnotate")), existing.ETag);
    }

    /// <summary>The request body the ACL endpoint takes — the full intended value, because it is a PUT.</summary>
    internal static object Body(AclRights rights) => new
    {
        canSee = rights.CanSee,
        canReadContent = rights.CanReadContent,
        canEditContent = rights.CanEditContent,
        canEditIndexData = rights.CanEditIndexData,
        canDelete = rights.CanDelete,
        canCreateSubItems = rights.CanCreateSubItems,
        canManagePermissions = rights.CanManagePermissions,
        canMove = rights.CanMove,
        canAnnotate = rights.CanAnnotate,
    };

    /// <summary>Reports what changed, naming the rights rather than the booleans.</summary>
    internal static void Report(
        IAnsiConsole console, Target target, AclRights before, AclRights after)
    {
        var added = AclRights.All.Where(r => after.Has(r) && !before.Has(r)).ToList();
        var removed = AclRights.All.Where(r => before.Has(r) && !after.Has(r)).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            console.MarkupLine(
                $"[grey]{Markup.Escape(target.PrincipalName)} already holds exactly that; nothing changed.[/]");
            return;
        }

        console.MarkupLine($"{Markup.Escape(target.PrincipalName)} ({target.PrincipalKind}):");
        if (added.Count > 0)
        {
            console.MarkupLine($"  [green]+ {Markup.Escape(string.Join(", ", added))}[/]");
        }

        if (removed.Count > 0)
        {
            console.MarkupLine($"  [red]- {Markup.Escape(string.Join(", ", removed))}[/]");
        }

        console.MarkupLine(after.Granted.Count == 0
            ? "  [grey]now holds nothing on this document[/]"
            : $"  [grey]now holds: {Markup.Escape(string.Join(", ", after.Granted))}[/]");
    }
}
