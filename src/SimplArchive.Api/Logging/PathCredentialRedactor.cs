using Serilog.Core;
using Serilog.Events;
using SimplArchive.Api.Modules;

namespace SimplArchive.Api.Logging;

/// <summary>
/// Replaces a path credential (ADR 0909) with <c>***</c> in every property of every log event of its request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an enricher as well as the middleware.</b> The middleware cuts the credential out of the path before
/// routing and before any of OUR logging, but the host has already captured the raw path by then: its "Request
/// starting" line, and the <c>RequestPath</c> scope that rides on every line of the request. Neither passes through
/// our code, so they are redacted here, on the way out.
/// </para>
/// <para>
/// <b>By the exact value, not by position.</b> The credential is known exactly: read from the raw path while the
/// request is young, and from <see cref="PathCredentialFeature"/> once the path was shortened. Replacing that string
/// leaves every other segment alone. A positional rule could not tell the raw path from the shortened one, and would
/// have redacted <c>repo</c> in <c>/fdroid/repo/…</c>. Only the request's own credential is replaced, so nothing
/// outside a request is touched (ADR 0886: never a secret; whitelist rather than hunt).
/// </para>
/// </remarks>
public sealed class PathCredentialRedactor(IHttpContextAccessor accessor, PathCredentialPrefixes prefixes) : ILogEventEnricher
{
    public const string Mask = "***";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (prefixes.IsEmpty || accessor.HttpContext is not { } context)
        {
            return;
        }

        var credential = context.Features.Get<PathCredentialFeature>()?.Credential ?? prefixes.CredentialIn(context.Request.Path);
        if (credential is null)
        {
            return;
        }

        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (Redact(value, credential) is { } redacted)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    /// <summary>The value with the credential replaced, or null when it does not contain it.</summary>
    public static LogEventPropertyValue? Redact(LogEventPropertyValue value, string credential) => value switch
    {
        ScalarValue { Value: { } scalar } when scalar.ToString() is { } text && text.Contains(credential, StringComparison.Ordinal) =>
            new ScalarValue(text.Replace(credential, Mask, StringComparison.Ordinal)),
        SequenceValue sequence when sequence.Elements.Any(e => Redact(e, credential) is not null) =>
            new SequenceValue(sequence.Elements.Select(e => Redact(e, credential) ?? e)),
        StructureValue structure when structure.Properties.Any(p => Redact(p.Value, credential) is not null) =>
            new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Value, credential) ?? p.Value)), structure.TypeTag),
        DictionaryValue dictionary when dictionary.Elements.Any(e => Redact(e.Value, credential) is not null) =>
            new DictionaryValue(dictionary.Elements.Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Redact(e.Value, credential) ?? e.Value))),
        _ => null,
    };
}
