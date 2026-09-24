using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>Where the installation is. Every command needs this and nothing else by default.</summary>
public class ApiSettings : CommandSettings
{
    [CommandOption("--url <URL>")]
    [Description("Base URL of the installation, e.g. https://archive.example.com")]
    public required string Url { get; init; }

    // Spectre builds settings by reflection and does not enforce `required` — validate explicitly.
    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Url)
        ? ValidationResult.Error("Missing required option --url.")
        : ValidationResult.Success();
}

/// <summary>
/// Settings for the acts only a PLATFORM ADMINISTRATOR may perform — creating a tenant, above all.
/// </summary>
/// <remarks>
/// Deliberately a distinct type rather than a flag on <see cref="ApiSettings"/>. ADR 0822 requires the two
/// principals to be visibly different at the call site: a command that quietly picked the wrong one would be
/// an escalation surprise rather than a failure, and the type is what makes "which principal is this?"
/// answerable by reading the command rather than by running it.
/// </remarks>
public class PlatformAdminSettings : ApiSettings
{
    [CommandOption("--client-id <ID>")]
    [Description("Platform-administrator client id (client-credentials grant).")]
    public required string ClientId { get; init; }

    [CommandOption("--client-secret <SECRET>")]
    [Description("Platform-administrator client secret. Prefer SACONSOLE_CLIENT_SECRET in the environment.")]
    public string? ClientSecret { get; init; }

    /// <summary>The secret, from the option or the environment — never echoed.</summary>
    public string ResolvedSecret => string.IsNullOrEmpty(ClientSecret)
        ? Environment.GetEnvironmentVariable("SACONSOLE_CLIENT_SECRET") ?? string.Empty
        : ClientSecret;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        { ClientId: null or "" } => ValidationResult.Error("Missing required option --client-id."),
        _ when string.IsNullOrEmpty(ResolvedSecret) => ValidationResult.Error(
            "No client secret. Pass --client-secret, or set SACONSOLE_CLIENT_SECRET so it does not land in shell history."),
        _ => ValidationResult.Success(),
    };
}
