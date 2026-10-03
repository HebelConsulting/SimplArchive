using System.ComponentModel;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public class ModuleSettingsSettings : TenantSessionSettings
{
    [CommandArgument(0, "<module>")]
    [Description("The module, by id — e.g. encryption. List them: saconsole module list")]
    public required string Module { get; init; }
}

/// <summary>
/// Reports what a module declared it needs configuring, and what is configured (#1504, ADR 0772).
/// </summary>
/// <remarks>
/// <b>A secret's value is never reported</b>, only whether one is set — which is what the resource itself
/// carries, so this tool cannot leak something the API declined to hand over.
/// </remarks>
public sealed class ModuleSettingsShowCommand(IAnsiConsole console) : AsyncCommand<ModuleSettingsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ModuleSettingsSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var row = ModuleSurface.Row(await ModuleSurface.ListingAsync(api, cancellationToken), settings.Module);
        var resource = await api.GetAsync(
            ModuleSurface.SettingsHref(row, settings.Module), cancellationToken);

        var items = resource.TryGetProperty("items", out var declared) ? declared.EnumerateArray().ToList() : [];
        if (items.Count == 0)
        {
            console.MarkupLine($"'{Markup.Escape(settings.Module)}' declares nothing to configure.");
            return 0;
        }

        var table = new Table().Border(TableBorder.None);
        table.AddColumn("key");
        table.AddColumn("value");
        table.AddColumn("kind");
        table.AddColumn("label");

        foreach (var item in items)
        {
            string Text(string name) =>
                item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? string.Empty
                    : string.Empty;
            bool Flag(string name) =>
                item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            var secret = Flag("isSecret");
            var hasValue = Flag("hasValue");
            var kind = Text("kind");
            var choices = item.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString()).Where(x => x is { Length: > 0 }).ToList()
                : [];

            var shown = secret
                ? hasValue ? "[grey]set (not shown)[/]" : "[grey]not set[/]"
                : Text("value") is { Length: > 0 } value ? Markup.Escape(value) : "[grey]not set[/]";

            table.AddRow(
                Markup.Escape(Text("key")),
                shown,
                choices.Count > 0
                    ? Markup.Escape($"{kind}: {string.Join(" | ", choices)}")
                    : Markup.Escape(kind),
                Markup.Escape(Text("label")));
        }

        console.Write(table);
        return 0;
    }
}

public sealed class ModuleSettingsSetSettings : ModuleSettingsSettings
{
    [CommandArgument(1, "<key>")]
    [Description("The setting to write, as the module declared it.")]
    public required string Key { get; init; }

    [CommandOption("--from-env")]
    [Description("Take the value from this environment variable.")]
    public string? FromEnvironment { get; init; }

    [CommandOption("--from-file")]
    [Description("Take the value from this file, trailing newline trimmed.")]
    public string? FromFile { get; init; }

    [CommandOption("--stdin")]
    [Description("Take the value from standard input.")]
    public bool FromStdin { get; init; }

    [CommandOption("--clear")]
    [Description("Remove the configured value.")]
    public bool Clear { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when Sources != 1 => ValidationResult.Error(
            "Give the value exactly one way: --from-env, --from-file, --stdin or --clear. "
            + "There is deliberately no --value: a value typed on the command line lands in shell history "
            + "and is visible to other processes, and a module setting may be a secret."),
        _ => ValidationResult.Success(),
    };

    private int Sources =>
        (string.IsNullOrWhiteSpace(FromEnvironment) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(FromFile) ? 0 : 1)
        + (FromStdin ? 1 : 0)
        + (Clear ? 1 : 0);
}

/// <summary>
/// Writes one of a module's settings (#1504, ADR 0772).
/// </summary>
/// <remarks>
/// <para>
/// <b>The value never arrives as an argument</b> (owner, 2026-10-01). A module setting may be a secret — the
/// core transit-encrypts those and never serves them back — and a value typed on a command line lands in
/// shell history and is visible in <c>ps</c> to anyone else on the host. This is the same reason the tool
/// takes its access token from the environment rather than as an option.
/// </para>
/// <para>
/// The choice is deliberately <b>not</b> "inline, unless the module declared this one secret": that relies on
/// the declaration being right, and an unmarked secret would walk straight through. Taking every value out of
/// argv costs a little typing for the non-secret case and cannot be got wrong.
/// </para>
/// <para>
/// <b>The write is a MERGE</b>, which is the endpoint's own shape: an absent key is left alone and an
/// explicit empty value clears one. So writing one setting cannot blank the others — which a full replace
/// would do to every secret, since a client cannot read one back to resend it.
/// </para>
/// </remarks>
public sealed class ModuleSettingsSetCommand(IAnsiConsole console) : AsyncCommand<ModuleSettingsSetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ModuleSettingsSetSettings settings, CancellationToken cancellationToken)
    {
        var value = await ValueAsync(settings, cancellationToken);

        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);

        var row = ModuleSurface.Row(await ModuleSurface.ListingAsync(api, cancellationToken), settings.Module);
        var address = ModuleSurface.SettingsHref(row, settings.Module);

        // READ FIRST, so an undeclared key is named here with the declared set beside it. The endpoint
        // refuses one too, but its refusal cannot list the alternatives — and a mistyped key is the likeliest
        // mistake, since the keys are a module's private vocabulary.
        var resource = await api.GetAsync(address, cancellationToken);
        var declared = (resource.TryGetProperty("items", out var items) ? items.EnumerateArray() : [])
            .Select(i => i.TryGetProperty("key", out var k) ? k.GetString() : null)
            .Where(k => k is { Length: > 0 })
            .ToList();

        if (!declared.Contains(settings.Key, StringComparer.Ordinal))
        {
            throw new CliException(
                $"'{settings.Module}' declares no setting '{settings.Key}'. "
                + (declared.Count == 0
                    ? "It declares nothing to configure."
                    : $"It declares: {string.Join(", ", declared)}."));
        }

        await api.PutAsync(
            address,
            new { values = new Dictionary<string, string?> { [settings.Key] = value } },
            cancellationToken);

        // THE VALUE IS NOT ECHOED — the whole point of taking it out of argv would be lost if the
        // confirmation printed it back.
        console.MarkupLine(settings.Clear
            ? $"[green]Cleared[/] {Markup.Escape(settings.Module)} / {Markup.Escape(settings.Key)}"
            : $"[green]Set[/] {Markup.Escape(settings.Module)} / {Markup.Escape(settings.Key)} "
              + $"[grey]({value!.Length} characters)[/]");

        return 0;
    }

    private static async Task<string?> ValueAsync(
        ModuleSettingsSetSettings settings, CancellationToken cancellationToken) => settings switch
        {
            { Clear: true } => string.Empty,
            { FromEnvironment: { Length: > 0 } variable } =>
                Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
                    ? value
                    : throw new CliException(
                        $"${variable} is not set, or is empty. Export it in this shell first — and prefer a form "
                        + "your shell does not record, such as reading it from a password manager."),
            { FromFile: { Length: > 0 } path } =>
                File.Exists(path)
                    ? (await File.ReadAllTextAsync(path, cancellationToken)).TrimEnd('\r', '\n')
                    : throw new CliException($"No file at '{path}'."),
            _ => (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n'),
        };
}
