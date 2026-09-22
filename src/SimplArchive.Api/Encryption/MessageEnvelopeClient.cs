namespace SimplArchive.Api.Encryption;

/// <summary>
/// The core's client for the per-installation encryption service's download leg (SimplArchiveEncryption
/// ADRs 0005/0007): hand it a built RFC-822 message and a recipient, get the message back with its body
/// replaced by CMS <c>EnvelopedData</c> to that user's registered certificate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unconfigured means inert.</b> <c>Encryption:ServiceUrl</c> is null by default, and with it null this
/// class answers null without a call — every existing surface behaves exactly as before the service
/// existed. The whole integration must stay invisible to an installation that has not opted in.
/// </para>
/// <para>
/// <b>The switch has a per-tenant half (ADR 0813).</b> <c>Encryption:Tenants</c> lists the tenant NAMES the
/// service applies to; absent or empty means every tenant, so an installation that only sets the URL keeps
/// the original per-installation behaviour. With the list present, an unlisted tenant answers null without
/// a call, exactly like an unconfigured installation — which is what lets one stack (the kiosk) run an
/// encrypted demo tenant beside an untouched public one.
/// </para>
/// <para>
/// <b>404 is a contract, not a failure</b>: the user has no registered certificate, and the POC serves
/// plaintext then (SimplArchiveEncryption ADR 0007's stated boundary — production turns that into a policy
/// decision). It is logged at Debug precisely because it is the expected state for most users.
/// </para>
/// <para>
/// <b>Everything else fails OPEN in milestone 1, and says so.</b> A service outage yields plaintext plus a
/// Warning naming the config key (ADR 0626's rule: name the switch). That is honest today because the blobs
/// ARE plaintext at rest in milestone 1 — the envelope adds transport protection, it does not yet guard a
/// secret the caller could not otherwise have. Milestone 2 (at-rest encryption) flips this to fail-closed
/// BY CONSTRUCTION: undecryptable blobs cannot be served plaintext by any code path, so no flag needs
/// flipping and no one can forget to.
/// </para>
/// </remarks>
public sealed class MessageEnvelopeClient(
    IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MessageEnvelopeClient> logger)
{
    internal const string HttpClientName = "encryption-service";

    /// <summary>Whether an encryption service applies to <paramref name="tenantName"/> — callers may use
    /// this to skip work (a stored-size shortcut, say) that would be wrong for an enveloped message.</summary>
    public bool EnabledFor(string tenantName) =>
        !string.IsNullOrEmpty(configuration["Encryption:ServiceUrl"]) && TenantListed(tenantName);

    /// <summary>The enveloped message, or null — meaning "serve what you built": not configured, tenant not
    /// listed, no certificate for this user, or (milestone 1 only) the service was unreachable.</summary>
    public async Task<byte[]?> TryEnvelopeAsync(
        string tenantName, string email, byte[] rfc822, CancellationToken cancellationToken)
    {
        if (configuration["Encryption:ServiceUrl"] is not { Length: > 0 } serviceUrl || !TenantListed(tenantName))
        {
            return null;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(
                $"{serviceUrl.TrimEnd('/')}/api/users/{Uri.EscapeDataString(email)}/enveloped",
                new ByteArrayContent(rfc822), cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogDebug("No certificate registered for {Email}; serving plaintext (the POC contract).", email);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception,
                "The encryption service (Encryption:ServiceUrl) did not envelope a message for {Email}; "
                + "serving plaintext — milestone 1 fails open because blobs are plaintext at rest anyway. "
                + "Trace carries the exchange.", email);
            return null;
        }
    }

    // The per-tenant half of the switch (ADR 0813). Read per call like ServiceUrl, so a config reload takes
    // effect without a restart. Whitespace-only entries are skipped: the compose passthrough
    // (`Encryption__Tenants__0: ${ENCRYPTION_TENANT:-}`) yields an empty element when the variable is unset,
    // and an empty element must mean "no list", not "a tenant named nothing".
    private bool TenantListed(string tenantName)
    {
        var listed = configuration.GetSection("Encryption:Tenants").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return listed.Count == 0 || listed.Contains(tenantName, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Registration, kept out of Program.cs so the integration is one line there.</summary>
public static class MessageEnvelopeServices
{
    public static IServiceCollection AddMessageEnvelope(this IServiceCollection services)
    {
        // A modest timeout: the envelope is CPU-light (one CMS wrap), so a slow answer means a sick service,
        // and an IMAP FETCH stalled behind a long HTTP timeout reads as a hung mail client.
        services.AddHttpClient(MessageEnvelopeClient.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<MessageEnvelopeClient>();
        return services;
    }
}
