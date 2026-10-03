using System.ComponentModel;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class CertificateRevokeSettings : TenantSessionSettings
{
    [CommandOption("--serial")]
    [Description("The certificate's serial — what a certification authority names it by.")]
    public string? Serial { get; init; }

    [CommandOption("--thumbprint")]
    [Description("The certificate's SHA-256 thumbprint, as the listing reports it.")]
    public string? Thumbprint { get; init; }

    [CommandOption("--on")]
    [Description("The revocation date (yyyy-MM-dd). Defaults to today, UTC.")]
    public string? On { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,

        // EXACTLY ONE, because the two identify the certificate differently and accepting both would leave
        // the question of what to do when they disagree — which is a question with no good answer.
        _ when string.IsNullOrWhiteSpace(Serial) == string.IsNullOrWhiteSpace(Thumbprint) =>
            ValidationResult.Error("Give exactly one of --serial or --thumbprint."),

        _ when On is { Length: > 0 } on && !DateOnly.TryParse(on, out _) =>
            ValidationResult.Error($"'{On}' is not a date. Use yyyy-MM-dd."),

        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Revokes one enrolled certificate (#1504), by the identifier the caller has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Revocation is an index field, not an endpoint</b> — the module's enrolment surface deliberately enrols
/// and lists and nothing else (its ADR 0005), and <c>Revoked on</c> is the only revocation interface. So this
/// writes that field on the certificate document, which is exactly what the pencil in the workbench does.
/// </para>
/// <para>
/// <b>Two identifiers because two callers have different ones.</b> A certification authority names what it
/// revoked by SERIAL and mentions a thumbprint nowhere; a person reading the listing has the thumbprint in
/// front of them. Both resolve through the same listing, and the walk that writes the field is shared with
/// the bulk import so the two cannot write it differently.
/// </para>
/// </remarks>
public sealed class CertificateRevokeCommand(IAnsiConsole console) : AsyncCommand<CertificateRevokeSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateRevokeSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var hypermedia = new Hypermedia(api);
        var address = await CertificateSurface.AddressAsync(hypermedia, cancellationToken);

        var listing = await api.GetAsync(address, cancellationToken);
        if (Find(listing, settings) is not { } found)
        {
            throw new CliException(settings.Serial is { Length: > 0 }
                ? $"Nothing enrolled in this tenant carries serial {settings.Serial}. "
                    + "Check it with: saconsole certificates list"
                : $"Nothing enrolled in this tenant carries thumbprint {settings.Thumbprint}.");
        }

        if (found.AlreadyRevoked)
        {
            // The state asked for already obtains. Not an error, and not silently "done" either — an operator
            // running this deliberately is entitled to know they changed nothing.
            console.MarkupLine($"[blue]Unchanged[/] — {Markup.Escape(found.Label)} was already revoked.");
            return 0;
        }

        var on = settings.On is { Length: > 0 } date
            ? DateOnly.Parse(date).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            : DateTime.UtcNow;

        await CertificateRevocation.SetRevokedOnAsync(
            api, hypermedia, found.DocumentId, new DateTimeOffset(on, TimeSpan.Zero), cancellationToken);

        console.MarkupLine($"[green]Revoked[/] {Markup.Escape(found.Label)} on {on:yyyy-MM-dd}.");
        console.MarkupLine(
            "Content is no longer enveloped to it. A reader whose only certificate this was can no longer "
            + "open sealed documents until another is enrolled.");

        return 0;
    }

    private static Enrolment? Find(JsonElement listing, CertificateRevokeSettings settings)
    {
        if (!listing.TryGetProperty("certificates", out var rows) && !listing.TryGetProperty("enrolled", out rows))
        {
            return null;
        }

        var serial = settings.Serial is { Length: > 0 } s ? CertificateManifest.NormaliseSerial(s) : null;
        var thumbprint = settings.Thumbprint?.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("documentId", out var id))
            {
                continue;
            }

            var rowSerial = row.TryGetProperty("serial", out var rs) ? CertificateManifest.NormaliseSerial(rs.GetString() ?? string.Empty) : string.Empty;
            var rowThumb = row.TryGetProperty("thumbprint", out var rt) ? (rt.GetString() ?? string.Empty).ToUpperInvariant() : string.Empty;

            var matches = serial is { Length: > 0 }
                ? string.Equals(rowSerial, serial, StringComparison.Ordinal)
                : string.Equals(rowThumb, thumbprint, StringComparison.Ordinal);

            if (matches)
            {
                return new Enrolment(
                    id.GetGuid(),
                    row.TryGetProperty("label", out var l) ? l.GetString() ?? "the certificate" : "the certificate",
                    row.TryGetProperty("revokedAt", out var at) && at.ValueKind is not JsonValueKind.Null);
            }
        }

        return null;
    }

    private sealed record Enrolment(Guid DocumentId, string Label, bool AlreadyRevoked);
}
