using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>
/// The HTTP edge of the tool: acquire a token, call the Api, turn a Problem Details body into a message a
/// person can act on.
/// </summary>
/// <remarks>
/// Takes an <see cref="HttpClient"/> rather than making one, so the failure paths — a refused token, a 403,
/// an RFC 7807 body — are testable without a server. They are the paths that matter: the happy path announces
/// itself, and a wrong-principal refusal is the one an administrator will actually meet.
/// </remarks>
public sealed class SimplArchiveApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Client-credentials token for a platform administrator (ADR 0206).</summary>
    public async Task AuthenticateAsPlatformAdministratorAsync(
        string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        });

        using var response = await http.PostAsync("connect/token", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The secret is never echoed, not even on failure — an error message is a place credentials leak.
            throw new CliException(
                $"The installation refused these platform-administrator credentials ({(int)response.StatusCode}). "
                + "Check --client-id, and that the secret belongs to a PlatformAdministrator rather than a service account.");
        }

        var token = JsonDocument.Parse(body).RootElement.TryGetProperty("access_token", out var value)
            ? value.GetString()
            : null;

        if (string.IsNullOrEmpty(token))
        {
            throw new CliException("The token endpoint answered without an access_token. The installation may not be a SimplArchive Api.");
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 401 is called out because it has ONE cause here and a generic status would send the reader
            // looking at rights instead: the session in the environment has expired, and there is no refresh
            // token by design (ADR 0823).
            throw new CliException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "The session has expired. Run 'saconsole login' again — this tool holds no refresh token by design."
                : Describe(response.StatusCode, body, path));
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<JsonElement> PostAsync<TRequest>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, payload, Json, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(Describe(response.StatusCode, body, path));
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Sends raw bytes — a certificate is a FILE, not a JSON field.
    /// </summary>
    /// <remarks>
    /// The certificate endpoint takes the PEM (or DER) as the request body rather than wrapped in a document,
    /// which is what lets the same bytes the card tool exported be sent unchanged. Base64-ing them into JSON
    /// would add a step for every caller and a way to get it wrong.
    /// </remarks>
    public async Task<JsonElement> PutBytesAsync(
        string path, byte[] payload, string contentType, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        using var response = await http.PutAsync(path, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(Describe(response.StatusCode, body, path));
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Deletes a resource; an empty body is a normal answer and not a failure.</summary>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(path, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(Describe(
                response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), path));
        }
    }

    /// <summary>Turns an RFC 7807 body into one line, falling back to the status when it is not one.</summary>
    internal static string Describe(System.Net.HttpStatusCode status, string body, string path)
    {
        var prefix = status == System.Net.HttpStatusCode.Forbidden
            // The single most likely mistake with two principals in one binary, so it gets named rather than
            // left as a bare 403.
            ? "Refused (403) — this action needs a different principal than the one used. "
            : $"The installation answered {(int)status} for {path}. ";

        try
        {
            var problem = JsonDocument.Parse(body).RootElement;
            var detail = problem.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var title = problem.TryGetProperty("title", out var t) ? t.GetString() : null;
            var code = problem.TryGetProperty("errorCode", out var c) ? c.GetString() : null;

            var message = detail ?? title;
            return code is null ? prefix + message : $"{prefix}{message} [{code}]";
        }
        catch (JsonException)
        {
            return prefix + (string.IsNullOrWhiteSpace(body) ? "No details were returned." : body);
        }
    }
}

/// <summary>An error with a message meant for the person running the command, not a stack trace.</summary>
public sealed class CliException(string message) : Exception(message);
