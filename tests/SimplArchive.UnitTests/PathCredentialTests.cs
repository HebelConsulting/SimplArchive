using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Events;
using SimplArchive.Api.Logging;
using SimplArchive.Api.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// A credential carried in a module's path (ABI 1.11, ADR 0909). The pieces; the proof that it reaches no log is
// PathCredentialLogTests, against the real app's real output.
public class PathCredentialTests
{
    // Composed, as a module mints one, rather than written out: a literal in the credential format reads as a leaked
    // key to the secret scanner.
    private static readonly string Credential = ModuleCredentialFormat.Compose(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), "s3cret");

    private static PathCredentialPrefixes Loaded(IModuleCredentialAuthenticator? authenticator = null, ILogger? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModuleCredentialAuthenticator>(authenticator ?? new RepoAuthenticator());
        var prefixes = new PathCredentialPrefixes();
        prefixes.Load(services.BuildServiceProvider(), [new ModuleLoader.LoadedModule(new RepoModule(), "/m/repo.dll")], logger ?? NullLogger.Instance);
        return prefixes;
    }

    [Fact]
    public void The_segment_after_a_declared_prefix_is_the_credential()
    {
        var prefixes = Loaded();

        Assert.Equal(Credential, prefixes.CredentialIn(new PathString($"/fdroid/{Credential}/repo/index-v2.json")));
        Assert.Equal(Credential, prefixes.CredentialIn(new PathString($"/FDROID/{Credential}")));
        Assert.Null(prefixes.CredentialIn(new PathString($"/nuget/{Credential}/v3/index.json")));   // a header-credential prefix
        Assert.Null(prefixes.CredentialIn(new PathString("/fdroid")));
        Assert.Null(prefixes.CredentialIn(new PathString("/fdroid/")));
    }

    [Fact]
    public async Task The_middleware_cuts_the_credential_out_and_keeps_it_for_the_authenticator()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = $"/fdroid/{Credential}/repo/index-v2.json";
        PathString seen = default;

        await new PathCredentialMiddleware(c => { seen = c.Request.Path; return Task.CompletedTask; }, Loaded()).InvokeAsync(context);

        Assert.Equal("/fdroid/repo/index-v2.json", seen.Value);
        Assert.Equal(Credential, context.Features.Get<PathCredentialFeature>()!.Credential);
        Assert.Equal("/fdroid/***/repo/index-v2.json", PathCredentialMiddleware.DisplayPath(context));
    }

    [Fact]
    public void A_prefix_the_module_did_not_claim_is_refused_by_name()
    {
        // One module stripping a segment out of another's paths would be a silent routing change for the other.
        var logger = new CapturingLogger();

        var prefixes = Loaded(new ClaimsSomeoneElsesPrefix(), logger);

        Assert.True(prefixes.IsEmpty);
        Assert.Contains(logger.Entries, e => e.Contains("'elsewhere'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_authenticator_that_cannot_be_constructed_turns_path_credentials_off_rather_than_the_host()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModuleCredentialAuthenticator>(_ => throw new InvalidOperationException("needs a request"));
        var logger = new CapturingLogger();
        var prefixes = new PathCredentialPrefixes();

        prefixes.Load(services.BuildServiceProvider(), [new ModuleLoader.LoadedModule(new RepoModule(), "/m/repo.dll")], logger);

        Assert.True(prefixes.IsEmpty);
        Assert.Contains(logger.Entries, e => e.Contains("could not be constructed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_redactor_replaces_the_credential_inside_nested_values_and_leaves_the_rest()
    {
        var nested = new StructureValue(
        [
            new LogEventProperty("Path", new ScalarValue($"/fdroid/{Credential}/repo")),
            new LogEventProperty("Items", new SequenceValue([new ScalarValue($"GET http://h/fdroid/{Credential}"), new ScalarValue("repo")])),
        ]);

        var redacted = PathCredentialRedactor.Redact(nested, Credential)!.ToString();

        Assert.DoesNotContain(Credential, redacted, StringComparison.Ordinal);
        Assert.Contains("/fdroid/***/repo", redacted, StringComparison.Ordinal);
        Assert.Null(PathCredentialRedactor.Redact(new ScalarValue("/fdroid/repo/index-v2.json"), Credential));   // untouched
    }

    // A module claiming fdroid (path credential) and nuget (header credential), in THIS assembly, which is how the core
    // matches an authenticator to its module.
    private sealed class RepoModule : IIndustryModule
    {
        public string ModuleId => "repo-module";

        public string DisplayName => "Repo module";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public IReadOnlyList<string> RootRoutePrefixes => ["fdroid", "nuget"];

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void DefineStateMachines(IStateMachineDefinitions machines)
        {
        }
    }

    private class RepoAuthenticator : IModuleCredentialAuthenticator
    {
        public IReadOnlyList<string> RoutePrefixes => ["fdroid", "nuget"];

        public virtual IReadOnlyList<string> PathCredentialPrefixes => ["fdroid"];

        public ModuleChallenge Challenge => new("Basic", "x");

        public Task<ModuleCredentialIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken) =>
            Task.FromResult<ModuleCredentialIdentity?>(null);
    }

    private sealed class ClaimsSomeoneElsesPrefix : RepoAuthenticator
    {
        public override IReadOnlyList<string> PathCredentialPrefixes => ["elsewhere"];
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }
}
