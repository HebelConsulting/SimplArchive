using System.Net;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.UnitTests;

/// <summary>
/// The guardrails and the failure messages — the parts of a CLI an administrator actually meets. The happy
/// path announces itself; a refusal is where a tool either explains itself or wastes someone's afternoon.
/// </summary>
public sealed class SaConsoleTests
{
    private static TenantCreateCommand.Settings Valid(string? secret = "s3cret", string url = "https://archive.example.com") =>
        new()
        {
            Url = url,
            ClientId = "platform-admin",
            ClientSecret = secret,
            Name = "Acme",
            AdministratorEmail = "admin@acme.example",
            AdministratorDisplayName = "Acme Admin",
            RepositoryName = "Acme Archive",
        };

    [Fact]
    public void A_url_is_required()
    {
        var result = Valid(url: "").Validate();

        Assert.False(result.Successful);
        Assert.Contains("--url", result.Message);
    }

    [Fact] // the secret must be available, but the option is not the only way to supply it
    public void A_missing_secret_says_how_to_supply_it_without_shell_history()
    {
        var previous = Environment.GetEnvironmentVariable("SACONSOLE_CLIENT_SECRET");
        Environment.SetEnvironmentVariable("SACONSOLE_CLIENT_SECRET", null);
        try
        {
            var result = Valid(secret: null).Validate();

            Assert.False(result.Successful);
            Assert.Contains("SACONSOLE_CLIENT_SECRET", result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SACONSOLE_CLIENT_SECRET", previous);
        }
    }

    [Fact]
    public void The_environment_supplies_the_secret_when_the_option_does_not()
    {
        var previous = Environment.GetEnvironmentVariable("SACONSOLE_CLIENT_SECRET");
        Environment.SetEnvironmentVariable("SACONSOLE_CLIENT_SECRET", "from-the-environment");
        try
        {
            var settings = Valid(secret: null);

            Assert.True(settings.Validate().Successful);
            Assert.Equal("from-the-environment", settings.ResolvedSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SACONSOLE_CLIENT_SECRET", previous);
        }
    }

    [Fact] // two principals in one binary: the wrong one is the likeliest mistake, so 403 is named, not bare
    public void A_403_says_the_principal_is_wrong_rather_than_just_forbidden()
    {
        var message = SimplArchiveApi.Describe(HttpStatusCode.Forbidden, "", "api/tenants");

        Assert.Contains("different principal", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_details_body_becomes_one_actionable_line()
    {
        const string body = """
            {"title":"Conflict","detail":"A tenant with that name already exists.","errorCode":"TENANT_NAME_CONFLICT"}
            """;

        var message = SimplArchiveApi.Describe(HttpStatusCode.Conflict, body, "api/tenants");

        Assert.Contains("A tenant with that name already exists.", message, StringComparison.Ordinal);
        Assert.Contains("TENANT_NAME_CONFLICT", message, StringComparison.Ordinal);
    }

    [Fact] // not every error body is RFC 7807 — a proxy's HTML must not become an unhandled JsonException
    public void A_body_that_is_not_problem_details_still_produces_a_message()
    {
        var message = SimplArchiveApi.Describe(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>", "api/tenants");

        Assert.Contains("502", message, StringComparison.Ordinal);
    }

    // ---- `me certificate` (#1353, ADR 0833) ----------------------------------------------------------
    //
    // The refusal is the feature here. Registering a certificate is one PUT; what an administrator actually
    // meets is a file that turns out to be the wrong file, and the useful moment to say so is BEFORE the
    // bytes leave the machine — at which point the reader can still see which path they named.

    [Fact]
    public void A_file_holding_a_private_key_is_refused_before_it_is_sent()
    {
        var pem = "-----BEGIN PRIVATE KEY-----\nMIIE...\n-----END PRIVATE KEY-----\n"u8.ToArray();

        var thrown = Assert.Throws<CliException>(
            () => CertificateRegisterCommand.RefusePrivateKey(pem, "/tmp/holder.key"));

        Assert.Contains("PRIVATE KEY", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("/tmp/holder.key", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_encrypted_private_key_is_refused_too()
    {
        // "BEGIN ENCRYPTED PRIVATE KEY" still contains the phrase, which is why the check is a substring
        // rather than an exact header match — a password on the key does not make it a certificate.
        var pem = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nMIIE...\n"u8.ToArray();

        Assert.Throws<CliException>(() => CertificateRegisterCommand.RefusePrivateKey(pem, "/tmp/x.pem"));
    }

    [Theory]
    [InlineData("/tmp/identity.p12")]
    [InlineData("/tmp/identity.pfx")]
    public void A_PKCS12_bundle_is_refused_by_extension(string path)
    {
        // Caught by NAME because its bytes are opaque — and it is exactly the file somebody who just
        // generated an identity reaches for, with the private key inside it.
        var thrown = Assert.Throws<CliException>(
            () => CertificateRegisterCommand.RefusePrivateKey([0x30, 0x82, 0x0a, 0x01], path));

        Assert.Contains("private key", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_public_certificate_passes()
    {
        // The anti-vacuous half: the guard must not refuse the thing it exists to accept.
        var pem = "-----BEGIN CERTIFICATE-----\nMIIC...\n-----END CERTIFICATE-----\n"u8.ToArray();

        CertificateRegisterCommand.RefusePrivateKey(pem, "/tmp/holder.pem");
    }

    [Fact]
    public void A_DER_certificate_passes_although_it_is_not_text()
    {
        // DER is binary, so the ASCII scan reads noise. It must not accidentally match, and the extension
        // must not be mistaken for a bundle.
        CertificateRegisterCommand.RefusePrivateKey([0x30, 0x82, 0x03, 0x1f, 0x30, 0x82], "/tmp/holder.der");
    }
}
