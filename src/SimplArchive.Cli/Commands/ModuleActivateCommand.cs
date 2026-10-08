using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class ModuleActivateSettings : TenantSessionSettings
{
    // Both spellings, ONE value placeholder: Spectre's `|` separates alias forms, and giving each its own
    // <FILE> throws CommandTemplateException("Multiple option values are not supported") — at RUNTIME, on
    // --help, which a compile could never catch. Both spellings exist because the codebase writes "licence"
    // and the API field is `licenseDocumentId`, so an administrator will reasonably type either.
    [CommandOption("--licence|--license <FILE>")]
    [Description("The vendor-signed licence file to file and activate.")]
    public required string Licence { get; init; }

    [CommandOption("--parent <DOCUMENT>")]
    [Description("Id of the document (a repository or folder) to file the licence in.")]
    public required string Parent { get; init; }

    [CommandOption("--module <ID>")]
    [Description("Activate only this module of the ones the licence names. Default: every one this installation has.")]
    public string? Module { get; init; }

    [CommandOption("--name <NAME>")]
    [Description("Name for the filed licence document. Default: a name naming the modules and the expiry.")]
    public string? Name { get; init; }
}

/// <summary>
/// Files a vendor-signed licence as a document and activates every module it names (#1474; ADRs 0740/0743/0845).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is here and not in the signer</b> (ADR 0852): signing is a VENDOR act with the private key,
/// while filing the artefact and pointing a module at it is an act of the tenant ADMINISTRATOR — and the
/// signer lives under <c>tools/</c>, which is withheld from the public mirror, so a command there could never
/// reach a customer. It closes a real gap rather than adding sugar: the module admin section in both clients
/// covers a human doing this, and nothing covered a SCRIPT, which is why the flight-school demo seeder grew
/// its own inline copy composing <c>api/…</c> paths against the rels rule.
/// </para>
/// <para>
/// <b><c>--parent</c> is required, deliberately.</b> ADR 0743 says the artefact is filed "wherever the
/// administrator chose; activation references it rather than inventing a location" — so a default location
/// invented here would contradict the decision the artefact's whole shape rests on. Renewal is the tempting
/// case (file it beside the last one), and it is declined for the same reason.
/// </para>
/// <para>
/// <b>Every module the licence names, not one per invocation.</b> A licence is ONE file naming a LIST
/// (ADR 0845), so requiring a module per run would make the ordinary case N commands and make partial
/// activation the default outcome — the state that is hardest to notice, because each module reports its own
/// absence as though the licence were wrong. <c>--module</c> narrows it when that is genuinely wanted.
/// </para>
/// </remarks>
public sealed class ModuleActivateCommand(ILogger<ModuleActivateCommand> logger) : AsyncCommand<ModuleActivateSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ModuleActivateSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Licence))
        {
            throw new CliException($"No such licence file: {settings.Licence}");
        }

        if (!Guid.TryParse(settings.Parent, out var parentId))
        {
            throw new CliException($"--parent must be a document id (a GUID); got '{settings.Parent}'.");
        }

        var bytes = await File.ReadAllBytesAsync(settings.Licence, cancellationToken);
        var claims = LicenceClaims.Read(bytes, settings.Licence);

        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new SimplArchiveApi(http);
        var hypermedia = new Hypermedia(api);

        // One read, many follows (ADR 0557): the modules resource carries every module's own `license` rel,
        // so this is read ONCE and every activation below follows from that response. Reached from the ROOT
        // (ModuleSurface.ListingAsync), so a service account administering modules can reach it too (ADR 0870).
        var modules = await ModuleSurface.ListingAsync(api, cancellationToken);

        var targets = ModuleTargets.Resolve(modules, claims.ModuleIds, settings.Module);
        if (targets.Activatable.Count == 0)
        {
            throw new CliException(Describe.NothingToActivate(claims, targets));
        }

        var (documentId, outcome) = await LicenceFiling.FileAsync(
            api, hypermedia, parentId, settings.Name ?? Describe.DefaultName(claims), bytes, cancellationToken);
        logger.LogInformation(outcome switch
        {
            LicenceFiling.Outcome.Filed => "Filed the licence as document {DocumentId}.",
            LicenceFiling.Outcome.NewVersion => "Filed the licence as a new version of document {DocumentId}.",
            _ => "Unchanged: document {DocumentId} already holds this licence.",
        }, documentId);

        foreach (var (moduleId, licenceHref) in targets.Activatable)
        {
            await api.PutAsync(licenceHref, new { licenseDocumentId = documentId }, cancellationToken);
            logger.LogInformation("Activated {Module}.", moduleId);
        }

        // Named, not counted: a module the licence entitles and this installation does not carry is the
        // quiet half of a partial activation, and the administrator is the only one who can tell whether it
        // is a missing deployment or a licence bought ahead of one.
        foreach (var moduleId in targets.NotInstalled)
        {
            logger.LogWarning(
                "Not activated {Module}: the licence entitles it, but this installation does not carry that module. "
                + "Deploy it and run this again.", moduleId);
        }

        logger.LogInformation("Support runs through {SupportEnd} (inclusive).", claims.SupportContractEnd);

        return 0;
    }
}

/// <summary>
/// The claims this tool needs from a licence file, read WITHOUT the module ABI.
/// </summary>
/// <remarks>
/// Deliberately a local shape rather than <c>SimplArchive.ModuleAbi.TenantLicense</c>: this is the customer's
/// CLI, and referencing the module ABI would couple the administrative tool to the module contract for three
/// fields it only echoes. The server verifies the signature and every claim, so nothing here is a check —
/// reading the file locally exists ONLY so the tool knows which modules to activate, and so a file that is
/// not a licence is refused before anything is filed.
/// </remarks>
internal sealed record LicenceClaims(IReadOnlyList<string> ModuleIds, string SupportContractEnd)
{
    internal static LicenceClaims Read(byte[] bytes, string path)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(bytes).RootElement;
        }
        catch (JsonException)
        {
            throw new CliException($"{path} is not JSON, so it is not a licence file.");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("moduleIds", out var ids)
            || ids.ValueKind != JsonValueKind.Array)
        {
            throw new CliException(
                $"{path} has no moduleIds array, so it is not a module licence. A SERVICE licence is a "
                + "different artefact and is not filed in a tenant (ADR 0845).");
        }

        var moduleIds = ids.EnumerateArray()
            .Select(id => id.GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        if (moduleIds.Count == 0)
        {
            throw new CliException($"{path} names no modules, so filing it would activate nothing.");
        }

        var end = root.TryGetProperty("supportContractEnd", out var value) ? value.GetString() : null;

        return new LicenceClaims(moduleIds, end ?? "(unstated)");
    }
}

/// <summary>Which of the licence's modules this installation can actually activate, and which it cannot.</summary>
/// <param name="Activatable">Named by the licence, carried here, and licensable by this caller.</param>
/// <param name="NotInstalled">Named by the licence and not activatable here.</param>
/// <param name="Installed">Every module this host carries, whatever the licence says.</param>
/// <param name="Licensable">Those of <paramref name="Installed"/> advertising a <c>license</c> rel.</param>
/// <remarks>
/// <b><paramref name="Installed"/> and <paramref name="Licensable"/> are separate on purpose</b>, and a test
/// is what forced them apart. A module is PRESENT whenever the host lists it; it is LICENSABLE only when it
/// advertises the <c>license</c> rel, which ADR 0543 withholds from a caller who may not use it — in
/// practice, anyone who is not a tenant administrator. Collapsing the two made "you are probably not an
/// administrator" unreachable: the modules were listed, so the count was non-zero, and the tool answered
/// with a module-mismatch message instead of the actual cause.
/// </remarks>
internal sealed record ModuleTargets(
    IReadOnlyList<(string ModuleId, string LicenceHref)> Activatable,
    IReadOnlyList<string> NotInstalled,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Licensable)
{
    /// <summary>
    /// Matches the licence's module ids against the installation's, ORDINALLY.
    /// </summary>
    /// <remarks>
    /// Case-sensitive, like every other module-id comparison on this ABI: an id is a key, not a display
    /// name. A case-insensitive match here would activate a module the vendor never named.
    /// </remarks>
    internal static ModuleTargets Resolve(JsonElement modules, IReadOnlyList<string> licensed, string? only)
    {
        var wanted = only is null
            ? licensed
            : licensed.Where(id => string.Equals(id, only, StringComparison.Ordinal)).ToList();

        if (only is not null && wanted.Count == 0)
        {
            throw new CliException(
                $"The licence does not name '{only}'. It entitles: {string.Join(", ", licensed)}.");
        }

        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        var present = new List<string>();

        if (modules.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var module in items.EnumerateArray())
            {
                if (module.TryGetProperty("moduleId", out var id) && id.GetString() is { } moduleId)
                {
                    present.Add(moduleId);

                    // The `license` rel is the activation address and is advertised per module (ADR 0719:
                    // one rel, the METHOD says the action — it is a PUT). Its ABSENCE is meaningful rather
                    // than an error to work around: ADR 0543 makes a missing rel "not available to you,
                    // here, now", which for this surface means the caller is not a tenant administrator.
                    if (Links(module).TryGetValue("license", out var href))
                    {
                        installed[moduleId] = href;
                    }
                }
            }
        }

        return new ModuleTargets(
            [.. wanted.Where(installed.ContainsKey).Select(id => (id, installed[id]))],
            [.. wanted.Where(id => !installed.ContainsKey(id))],
            present,
            [.. installed.Keys]);
    }

    private static Dictionary<string, string> Links(JsonElement resource)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);

        if (resource.TryGetProperty("links", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in array.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && link.TryGetProperty("href", out var href)
                    && rel.GetString() is { } name && href.GetString() is { } address)
                {
                    links.TryAdd(name, address);
                }
            }
        }

        return links;
    }
}
