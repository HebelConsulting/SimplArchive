namespace SimplArchive.ModuleAbi;

/// <summary>
/// Authenticates the credentials a PROTOCOL client sends to this module's root-level routes (ABI 1.7, core ADR
/// 0898): a package manager's Basic auth, a bearer token or an API-key header, where there is no login and no
/// ambient tenant. Register one from <see cref="IIndustryModule.ConfigureServices"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The core does everything before the lookup.</b> It reads the credential from the request, parses its
/// <see cref="ModuleCredentialFormat">tenant-naming format</see>, sets that tenant, runs the activation gate, and
/// makes the request act as this module's principal. Only then is <see cref="AuthenticateAsync"/> called, with the
/// SECRET alone, so the module looks it up in its own tenant through the ordinary facade and never across tenants.
/// </para>
/// <para>
/// <b>Exclusive and protocol-neutral.</b> On a matching route the core's own login is not consulted, and nothing
/// here is specific to one protocol: the module names its API-key headers and its challenge, so a feed and a
/// container registry need nothing different from the core.
/// </para>
/// </remarks>
public interface IModuleCredentialAuthenticator
{
    /// <summary>The path prefixes (without a leading slash, e.g. <c>nuget</c>) whose requests authenticate here.
    /// Each must lie under one of the module's <see cref="IIndustryModule.RootRoutePrefixes"/>.</summary>
    IReadOnlyList<string> RoutePrefixes { get; }

    /// <summary>Headers carrying an API key, read after <c>Authorization</c> (e.g. <c>X-NuGet-ApiKey</c>).
    /// Default none: the core reads Basic (the password) and Bearer itself.</summary>
    IReadOnlyList<string> ApiKeyHeaders => [];

    /// <summary>What a refusal answers in <c>WWW-Authenticate</c>, so the client knows to retry with credentials.</summary>
    ModuleChallenge Challenge { get; }

    /// <summary>The identity the secret belongs to, or null to refuse. Called with the tenant set, the activation
    /// gate passed and the module's principal acting, so the facade answers. Never log the secret.</summary>
    Task<ModuleCredentialIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken);
}

/// <summary>The <c>WWW-Authenticate</c> challenge of a refusal: <c>{Scheme} realm="{Realm}"</c>.</summary>
public sealed record ModuleChallenge(string Scheme, string Realm);

/// <summary>Who a credential belongs to.</summary>
/// <param name="Subject">The module's own key for the caller (e.g. a customer folder's id). The core never
/// interprets it; the module's controllers read it from <see cref="IModuleCredentialContext"/>.</param>
/// <param name="PrincipalLabel">A human label for the log. Never a secret.</param>
public sealed record ModuleCredentialIdentity(string Subject, string PrincipalLabel);

/// <summary>The identity the current request authenticated as, for a module controller to read (inject it).
/// Null outside a credential-authenticated route.</summary>
public interface IModuleCredentialContext
{
    /// <summary>The authenticated identity, or null.</summary>
    ModuleCredentialIdentity? Identity { get; }
}

/// <summary>
/// The credential format the CORE owns (ABI 1.7, core ADR 0898): <c>sa_&lt;tenantId&gt;_&lt;secret&gt;</c>. The tenant
/// travels in the credential, so a protocol request finds its tenant without a login and without any lookup across
/// tenants. A module mints credentials with <see cref="Compose"/>; the core parses them with <see cref="TryParse"/>.
/// </summary>
public static class ModuleCredentialFormat
{
    /// <summary>What every credential in this format starts with.</summary>
    public const string Prefix = "sa_";

    /// <summary>The credential a client is given: the tenant in lowercase <c>N</c> form, then the secret.</summary>
    public static string Compose(Guid tenantId, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return $"{Prefix}{tenantId:N}_{secret}";
    }

    /// <summary>Splits a presented credential into its tenant and secret; false for anything else.</summary>
    public static bool TryParse(string? credential, out Guid tenantId, out string secret)
    {
        tenantId = Guid.Empty;
        secret = string.Empty;
        if (credential is null || !credential.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = credential.AsSpan(Prefix.Length);
        var separator = rest.IndexOf('_');
        if (separator <= 0 || separator == rest.Length - 1 || !Guid.TryParseExact(rest[..separator], "N", out tenantId))
        {
            tenantId = Guid.Empty;
            return false;
        }

        secret = rest[(separator + 1)..].ToString();
        return true;
    }
}
