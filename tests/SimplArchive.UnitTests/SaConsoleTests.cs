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
}
