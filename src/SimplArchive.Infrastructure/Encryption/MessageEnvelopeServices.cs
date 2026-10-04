using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.Infrastructure.Encryption;

/// <summary>Registration, kept out of Program.cs so the integration is one line there.</summary>
public static class MessageEnvelopeServices
{
    public static IServiceCollection AddMessageEnvelope(this IServiceCollection services)
    {
        // In-process enveloping to the reader's certificates (#1332). Its sibling — the encryption service's
        // `/enveloped` endpoint and the certificate registry behind it — is retired (ADR 0890).
        services.AddSingleton<SmimeMessageEnveloper>();
        return services;
    }
}
