using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class ModuleUploadSettings : ApiSettings
{
    /// <summary>Environment variable carrying the module credential, for a pipeline that has no key file.</summary>
    public const string KeyVariable = "SA_MODULE_KEY";

    [CommandArgument(0, "<FILE>")]
    [Description("The file to upload.")]
    public required string File { get; init; }

    [CommandOption("--rel <REL>")]
    [Description("The module's upload rel, as the API root advertises it (e.g. licensing:app-push).")]
    public required string Rel { get; init; }

    [CommandOption("--key-file <FILE>")]
    [Description($"A file holding the module credential the endpoint expects (keep it mode 600). Defaults to ${KeyVariable}.")]
    public string? KeyFile { get; init; }

    /// <summary>The credential: from the file if one is named, else the environment. Never an option value, so it
    /// cannot land in shell history or a logged command line (ADR 0886).</summary>
    public string ResolvedKey => string.IsNullOrWhiteSpace(KeyFile)
        ? Environment.GetEnvironmentVariable(KeyVariable)?.Trim() ?? string.Empty
        : System.IO.File.ReadAllText(KeyFile).Trim();

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(Rel) => ValidationResult.Error("Name the upload rel with --rel."),
        _ when !System.IO.File.Exists(File) => ValidationResult.Error($"No such file: {File}"),
        _ when !string.IsNullOrWhiteSpace(KeyFile) && !System.IO.File.Exists(KeyFile) => ValidationResult.Error($"No such key file: {KeyFile}"),
        _ when string.IsNullOrEmpty(ResolvedKey) => ValidationResult.Error($"No module credential. Pass --key-file, or set ${KeyVariable}."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Uploads a file to a module's upload rel (#1703, ADR 0918): the core's presigned upload as a module offers it
/// (ABI 1.12) — begin, PUT straight to storage, commit — for a release pipeline holding a module credential
/// rather than a login. First use: an Android app pushed to the vendor's app repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generic on purpose</b>, like <c>module action</c>: the rel is the caller's, the offer's shape is the ABI's
/// (<c>ModuleUploadProtocol</c>), so this tool names no module's endpoint and the next module with an upload needs
/// nothing here.
/// </para>
/// <para>
/// <b>Found by hypermedia without a login.</b> The root is read ANONYMOUSLY: an upload rel is installation-wide
/// (<c>ModuleRootLink.InstallationWide</c>), because a push key names a tenant but is no session and could never
/// see a tenant's rels. The commit address comes from the offer's <c>commit</c> link, never composed from the
/// upload id. The credential goes only to the module's two requests, never to the root or to storage.
/// </para>
/// </remarks>
public sealed class ModuleUploadCommand(ILogger<ModuleUploadCommand> logger) : AsyncCommand<ModuleUploadSettings>
{
    // The ABI's ModuleUploadProtocol, spelled here because this tool references no server assembly; an end-to-end
    // test holds the two spellings equal.
    public const string UrlField = "url";
    public const string CommitRel = "commit";

    protected override async Task<int> ExecuteAsync(
        CommandContext context, ModuleUploadSettings settings, CancellationToken cancellationToken)
    {
        var bytes = await System.IO.File.ReadAllBytesAsync(settings.File, cancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri($"{settings.ResolvedUrl.TrimEnd('/')}/") };
        var filed = await UploadAsync(http, settings.Rel, settings.ResolvedKey, bytes, logger, cancellationToken);

        // DATA output (ADR 0906): the module's answer to the filing, for the pipeline to capture; nothing else
        // goes to stdout.
        Console.Out.WriteLine(filed);
        return 0;
    }

    /// <summary>The three steps against an installation whose root <paramref name="http"/> reaches without a login;
    /// answers the module's response to the commit.</summary>
    public static async Task<string> UploadAsync(
        HttpClient http, string rel, string key, byte[] bytes, ILogger logger, CancellationToken cancellationToken)
    {
        var begin = await new Hypermedia(new SimplArchiveApi(http)).RootHrefAsync(rel, cancellationToken);
        var offer = JsonDocument.Parse(await SendAsync(http, begin, key, cancellationToken)).RootElement;
        var url = offer.TryGetProperty(UrlField, out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (url is null)
        {
            throw new CliException($"'{rel}' answered without an upload address ('{UrlField}'). Is it an upload rel?");
        }

        var commit = Hypermedia.Href(Hypermedia.LinksOf(offer), CommitRel, $"The upload offer of '{rel}'");
        logger.LogInformation("Uploading {Bytes} bytes for {Rel}", bytes.Length, rel);
        await SimplArchiveApi.UploadAsync(new Uri(url), bytes, cancellationToken);
        var filed = await SendAsync(http, commit, key, cancellationToken);
        logger.LogInformation("{Rel} filed the upload", rel);
        return filed;
    }

    // A module's request: POST with the credential, as the module's own header scheme takes it.
    private static async Task<string> SendAsync(HttpClient http, string href, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, href);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? body
            : throw new CliException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "The module refused the credential (401). Check the key file or $SA_MODULE_KEY."
                : SimplArchiveApi.Describe(response.StatusCode, body, href));
    }
}
