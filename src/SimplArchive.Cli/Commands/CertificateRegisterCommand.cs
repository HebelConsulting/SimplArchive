using System.ComponentModel;
using System.Text;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class CertificateRegisterSettings : UserSessionSettings
{
    [CommandArgument(0, "<file>")]
    [Description("The certificate file to register — PEM (.pem/.crt) or DER (.cer/.der).")]
    public required string File { get; init; }
}

/// <summary>
/// Registers the caller's own certificate from a file (#1353, ADR 0833).
/// </summary>
/// <remarks>
/// <para>
/// A FILE, deliberately, and not a card. Preparing a hardware token is card-applet work that no PKCS#11 tool
/// can do — the token advertises no key generation, and a PKCS#11 module cannot even see a PIV slot until it
/// carries a certificate. The card's own tool therefore generates the key and exports the certificate, and
/// this registers the bytes it produced. Reading cards here would also cost this tool its single
/// cross-platform package, since PKCS#11 needs a Windows-specific target framework that <c>PackAsTool</c>
/// refuses.
/// </para>
/// <para>
/// <b>Only the public certificate travels.</b> The server refuses key material, and so does this: a file that
/// looks like a private key is rejected before it is sent, because the useful moment to say "that is not what
/// you want to hand over" is before it leaves the machine.
/// </para>
/// </remarks>
public sealed class CertificateRegisterCommand(IAnsiConsole console) : AsyncCommand<CertificateRegisterSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateRegisterSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            throw new CliException($"No such file: {settings.File}");
        }

        var bytes = await File.ReadAllBytesAsync(settings.File, cancellationToken);
        RefusePrivateKey(bytes, settings.File);

        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var address = await CertificateEndpoint.AddressAsync(api, cancellationToken);
        var status = await api.PutBytesAsync(address, bytes, ContentTypeFor(settings.File), cancellationToken);

        var subject = status.TryGetProperty("subject", out var value) ? value.GetString() : null;
        console.MarkupLine($"[green]Registered[/] {Markup.Escape(subject ?? "the certificate")}.");
        console.MarkupLine(
            "Content addressed to this user is now enveloped to it. "
            + "Replace it by deleting it first: [blue]saconsole me certificate delete[/].");

        return 0;
    }

    /// <summary>
    /// Refuses a file containing key material, before anything is sent.
    /// </summary>
    /// <remarks>
    /// The server refuses it too, so this is not the security boundary — it is the MESSAGE. A private key
    /// reaching an HTTP request is worth stopping where the reader can still see which file they named, and
    /// "that file is a private key" is a better answer than a 400 about a certificate that could not be
    /// parsed. PKCS#12 is caught by extension because its bytes are opaque: a `.p12` is exactly the file
    /// somebody who just generated an identity will reach for, and it carries the key.
    /// </remarks>
    internal static void RefusePrivateKey(byte[] bytes, string path)
    {
        var text = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));

        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new CliException(
                $"{path} contains a PRIVATE KEY. Register only the public certificate — "
                + "the key must never leave the machine or card that holds it.");
        }

        if (Path.GetExtension(path) is ".p12" or ".pfx")
        {
            throw new CliException(
                $"{path} is a PKCS#12 file, which holds the private key as well as the certificate. "
                + "Export the certificate on its own and register that.");
        }
    }

    /// <summary>PEM or DER, decided by extension — the server accepts both and parses what arrives.</summary>
    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".der" or ".cer" => "application/pkix-cert",
        _ => "application/x-pem-file",
    };
}
