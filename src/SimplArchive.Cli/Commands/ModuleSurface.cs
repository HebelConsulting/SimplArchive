using System.Text.Json;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>Reaching a module's row and its settings, by rels (#1504).</summary>
internal static class ModuleSurface
{
    /// <summary>root → tenant settings → modules. Every hop a rel.</summary>
    internal static async Task<JsonElement> ListingAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var tenantSettings = await hypermedia.RootHrefAsync("tenantSettings", cancellationToken);
        var modules = Hypermedia.Href(
            await hypermedia.LinksOfAsync(tenantSettings, cancellationToken), "modules", "The tenant settings");

        return await api.GetAsync(modules, cancellationToken);
    }

    /// <summary>The rows, as the listing spells them.</summary>
    internal static IEnumerable<JsonElement> Rows(JsonElement listing) =>
        listing.TryGetProperty("items", out var rows) ? rows.EnumerateArray() : [];

    /// <summary>One module's row, or a refusal that lists what this installation has.</summary>
    internal static JsonElement Row(JsonElement listing, string moduleId)
    {
        foreach (var row in Rows(listing))
        {
            if (row.TryGetProperty("moduleId", out var id)
                && string.Equals(id.GetString(), moduleId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        var installed = Rows(listing)
            .Select(r => r.TryGetProperty("moduleId", out var id) ? id.GetString() : null)
            .Where(id => id is { Length: > 0 })
            .ToList();

        throw new CliException(
            $"This installation has no module '{moduleId}'. "
            + (installed.Count == 0
                ? "It has no modules installed at all."
                : $"It has: {string.Join(", ", installed)}."));
    }

    /// <summary>
    /// The address of a module's settings — and a refusal naming the reason when there is none.
    /// </summary>
    /// <remarks>
    /// <b>A missing rel is the answer</b> (ADR 0543). The <c>settings</c> rel is advertised only where the
    /// module is active and declares something to configure, and those are two different reasons with two
    /// different fixes — so the refusal distinguishes them rather than saying "not found" for both.
    /// </remarks>
    internal static string SettingsHref(JsonElement row, string moduleId)
    {
        if (Hypermedia.LinksOf(row).TryGetValue("settings", out var href))
        {
            return href;
        }

        var active = row.TryGetProperty("active", out var flag) && flag.ValueKind == JsonValueKind.True;

        throw new CliException(active
            ? $"'{moduleId}' is active but declares nothing to configure, so it has no settings."
            : $"'{moduleId}' is not active for this tenant, so its settings are not available. "
              + "File its licence first: saconsole module activate.");
    }
}
