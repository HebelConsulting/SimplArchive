using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class ModuleActionSettings : TenantSessionSettings
{
    [CommandOption("--document <DOCUMENT>")]
    [Description("Id of the document the action is offered on.")]
    public required string Document { get; init; }

    [CommandOption("--rel <REL>")]
    [Description("The action's rel, as the document advertises it (e.g. licensing:issue-pull).")]
    public required string Rel { get; init; }

    [CommandOption("--value <VALUE>")]
    [Description("The option to commit. Default: the only option the action offers; with several, they are listed.")]
    public string? Value { get; init; }

    [CommandOption("--reveal-to <FILE>")]
    [Description("Where a value the action reveals once is written (mode 600; never overwritten). Default: ./<rel>.revealed")]
    public string? RevealTo { get; init; }

    [CommandOption("--if-offered")]
    [Description("Succeed without acting when the document does not offer the action (a re-run after it was done).")]
    public bool IfOffered { get; init; }
}

/// <summary>
/// Invokes a module's action on a document (ADR 0786) — the pick-then-act surface both clients render — for a script.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generic on purpose.</b> The action is found among the document's <c>moduleActions</c> by its rel and
/// committed at the <c>commitHref</c> it carries, with its own <c>valueField</c>; the options come from its
/// <c>optionsHref</c>, read the way the desktop reads them (the first array of objects). Nothing here knows a
/// module, so every module's actions are scriptable the day they ship.
/// </para>
/// <para>
/// <b>A revealed value goes to a FILE, never to the terminal</b> (ABI 1.8, ADR 0899): it is shown once, so the
/// command is the only chance to keep it — and a terminal is the worst place to keep it (scrollback, and over SSH
/// somebody's session log). The file is created mode 600 and never overwritten, and that is checked BEFORE the
/// action commits, so a path that cannot be written can never cost the only copy. Everything a person reads goes
/// to stderr.
/// </para>
/// <para>
/// <b><c>--if-offered</c></b> exists because a module withdraws an action once it is done (a credential already
/// issued is not issued twice), so a re-run of a bootstrap finds it missing. Without the flag that is an error,
/// which is right for a person who expected the action; with it, a script's second run is a no-op.
/// </para>
/// </remarks>
public sealed class ModuleActionCommand : AsyncCommand<ModuleActionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ModuleActionSettings settings, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(settings.Document, out var documentId))
        {
            throw new CliException($"--document must be a document id (a GUID); got '{settings.Document}'.");
        }

        var human = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);

        await InvokeAsync(
            new SimplArchiveApi(http), documentId, settings.Rel, settings.Value, settings.RevealTo, settings.IfOffered, human, cancellationToken);
        return 0;
    }

    /// <summary>What happened, for a script (and a test) to tell apart without parsing the human output.</summary>
    public enum Outcome
    {
        /// <summary>The document does not offer the action, and <c>--if-offered</c> said that is fine.</summary>
        NotOffered,

        /// <summary>Committed; the action revealed nothing.</summary>
        Done,

        /// <summary>Committed; the revealed value is in the file.</summary>
        Revealed,
    }

    public static async Task<Outcome> InvokeAsync(
        SimplArchiveApi api,
        Guid documentId,
        string rel,
        string? value,
        string? revealTo,
        bool ifOffered,
        IAnsiConsole human,
        CancellationToken cancellationToken)
    {
        var template = await new Hypermedia(api).RootHrefAsync("document", cancellationToken);
        var document = await api.GetAsync(template.Replace("{id}", documentId.ToString("D"), StringComparison.Ordinal), cancellationToken);

        if (Find(document, rel) is not { } action)
        {
            if (ifOffered)
            {
                human.MarkupLine($"Document {documentId:D} does not offer [blue]{Markup.Escape(rel)}[/]; nothing to do.");
                return Outcome.NotOffered;
            }

            throw new CliException(
                $"Document {documentId:D} does not offer '{rel}'. It offers: {Offered(document)}. "
                + "A module withdraws an action once done, or from a caller who may not use it.");
        }

        // Checked before committing: once the module has revealed the value, there is no second chance to keep it.
        var target = Path.GetFullPath(revealTo ?? $"{SafeFileName(action.Rel)}.revealed");
        if (File.Exists(target))
        {
            throw new CliException($"{target} already exists, and a revealed value is never written over another. Move it or pass --reveal-to.");
        }

        if (Path.GetDirectoryName(target) is { } directory && !Directory.Exists(directory))
        {
            throw new CliException($"{directory} does not exist, so a revealed value could not be kept there.");
        }

        var chosen = value ?? await OnlyOptionAsync(api, action, cancellationToken);
        var result = await api.PostAsync(action.CommitHref, new Dictionary<string, string> { [action.ValueField] = chosen }, cancellationToken);

        if (result.ValueKind != JsonValueKind.Object)
        {
            return Outcome.Done;
        }

        if (result.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
        {
            human.MarkupLine(Markup.Escape(text));
        }

        if (result.TryGetProperty("revealOnce", out var reveal) && reveal.ValueKind == JsonValueKind.Object
            && reveal.TryGetProperty("value", out var revealed) && revealed.GetString() is { Length: > 0 } secret)
        {
            var label = reveal.TryGetProperty("label", out var l) ? l.GetString() : null;
            Keep(target, secret);
            human.MarkupLine($"{Markup.Escape(label ?? "The revealed value")} written to [blue]{Markup.Escape(target)}[/] (mode 600). It is not shown again.");
            return Outcome.Revealed;
        }

        return Outcome.Done;
    }

    /// <summary>Creates the file NEW (never truncating one) with owner-only access, then writes the value.</summary>
    private static void Keep(string path, string secret)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(secret);
    }

    private static string SafeFileName(string rel) =>
        string.Concat(rel.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    internal sealed record Action(string Rel, string OptionsHref, string CommitHref, string ValueField);

    internal static Action? Find(JsonElement document, string rel)
    {
        if (!document.TryGetProperty("moduleActions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        return actions.EnumerateArray()
            .Where(a => Text(a, "rel") == rel)
            .Select(a => new Action(Text(a, "rel"), Text(a, "optionsHref"), Text(a, "commitHref"), Text(a, "valueField")))
            .FirstOrDefault();
    }

    private static string Offered(JsonElement document) =>
        document.TryGetProperty("moduleActions", out var actions) && actions.ValueKind == JsonValueKind.Array
            && actions.GetArrayLength() > 0
            ? string.Join(", ", actions.EnumerateArray().Select(a => a.GetProperty("rel").GetString()))
            : "no module actions";

    /// <summary>The one option the action offers; several are listed rather than one guessed.</summary>
    private static async Task<string> OnlyOptionAsync(SimplArchiveApi api, Action action, CancellationToken cancellationToken)
    {
        var options = Options(await api.GetAsync(action.OptionsHref, cancellationToken));

        return options switch
        {
            [var only] => only.Value,
            [] => throw new CliException($"'{action.Rel}' offers no option to commit right now."),
            _ => throw new CliException(
                $"'{action.Rel}' offers several options; name one with --value:\n"
                + string.Join("\n", options.Select(o => $"  {o.Value}  {o.Label}"))),
        };
    }

    /// <summary>The first array of objects in the options response — the module names it, so no name is assumed.</summary>
    internal static IReadOnlyList<(string Value, string Label)> Options(JsonElement response)
    {
        static string? Field(JsonElement e, params string[] names) =>
            names.Select(n => e.TryGetProperty(n, out var v) ? v.GetString() : null).FirstOrDefault(v => v is not null);

        if (response.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var property in response.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var options = property.Value.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.Object)
                .Select(i => (Value: Field(i, "value", "email", "id") ?? string.Empty, Label: Field(i, "label", "name") ?? string.Empty))
                .Where(o => o.Value.Length > 0)
                .ToList();
            if (options.Count > 0)
            {
                return options;
            }
        }

        return [];
    }
}
