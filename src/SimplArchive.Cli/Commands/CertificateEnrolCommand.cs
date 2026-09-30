using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class CertificateEnrolSettings : UserSessionSettings
{
    [CommandArgument(0, "<file>")]
    [Description("The certificate to enrol — PEM (.pem/.crt) or DER (.cer/.der). A chain is accepted.")]
    public required string File { get; init; }

    [CommandOption("--holder")]
    [Description("Whose certificate this is, by e-mail. Omitted enrols it for the caller.")]
    public string? Holder { get; init; }

    [CommandOption("--label")]
    [Description("What the holder calls this key: \"YubiKey 5C\", \"work laptop\".")]
    public string? Label { get; init; }
}

/// <summary>
/// Enrols ONE reader certificate (#1504) — the single-certificate sibling of <c>certificates import</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists because enrolling one certificate should not require writing a manifest, and because this is what
/// <c>curl</c> was being used for — which is how the module's missing root links went unnoticed for weeks: a
/// hand-written <c>POST</c> reaches an endpoint no rel advertises, and nothing complains.
/// </para>
/// <para>
/// This is the READER certificate documents are addressed to, not the self-service mail certificate
/// <c>saconsole me certificate register</c> writes. Two stores; this is the one content delivery uses.
/// </para>
/// </remarks>
public sealed class CertificateEnrolCommand(IAnsiConsole console) : AsyncCommand<CertificateEnrolSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateEnrolSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            throw new CliException($"No such file: {settings.File}");
        }

        var pem = PemOf(await File.ReadAllBytesAsync(settings.File, cancellationToken), settings.File);

        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var certificates = await CertificateSurface.AddressAsync(new Hypermedia(api), cancellationToken);

        try
        {
            var enrolled = await api.PostAsync(
                certificates,
                new { certificatePem = pem, label = settings.Label ?? string.Empty, holder = settings.Holder },
                cancellationToken);

            var label = enrolled.TryGetProperty("label", out var l) ? l.GetString() : null;
            var serial = enrolled.TryGetProperty("serial", out var s) ? s.GetString() : null;

            console.MarkupLine($"[green]Enrolled[/] {Markup.Escape(label ?? "the certificate")}"
                + (serial is { Length: > 0 } ? $" — serial {Markup.Escape(serial)}" : string.Empty));
            console.MarkupLine("Content addressed to this reader is now enveloped to it as well.");

            return 0;
        }
        catch (CliException exception) when (exception.Message.Contains(
            "ENCRYPTION_CERTIFICATE_ALREADY_ENROLLED", StringComparison.Ordinal))
        {
            // NOT a failure worth a non-zero exit on its own terms — the state the caller asked for already
            // obtains. Reported as unchanged, with the server's own sentence, which says WHO holds it when
            // that is somebody else (#1494).
            console.MarkupLine($"[blue]Unchanged[/] — {Markup.Escape(exception.Message)}");
            return 0;
        }
    }

    /// <summary>
    /// The file as PEM, refusing a private key before anything leaves this machine.
    /// </summary>
    /// <remarks>
    /// The server refuses key material too, and so does the module's own policy — but the useful moment to
    /// say "that is not what you want to hand over" is while the file is still in front of the person who
    /// chose it. DER is converted rather than refused: a certificate exported from a card tool is as likely
    /// to be one as the other, and refusing it would be refusing the format rather than the content.
    /// </remarks>
    private static string PemOf(byte[] bytes, string path)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new CliException(
                $"{path} contains a PRIVATE KEY. Enrol the public certificate only — a private key filed into "
                + "the archive is readable by everyone who can read the document, and it is the one thing "
                + "that must never leave the card or keystore that generated it.");
        }

        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            return text;
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(bytes);
            return certificate.ExportCertificatePem();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new CliException($"{path} is neither a PEM nor a DER certificate.");
        }
    }
}
