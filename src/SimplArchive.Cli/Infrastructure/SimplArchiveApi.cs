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

    /// <summary>
    /// A <c>GET</c> that also hands back the resource's <c>ETag</c> — for a caller about to mutate it.
    /// </summary>
    /// <remarks>
    /// <b>The tag from the read that produced the value is the only honest precondition</b> (ADR 0794). A tag
    /// re-read immediately before writing asserts "I edited what was there a millisecond ago", which is
    /// always true and can essentially never fail; this one says "I edited what I read", which is what
    /// detects the case the mechanism exists for. Every mutation on this API requires it — a missing
    /// <c>If-Match</c> answers 428, not 200.
    /// </remarks>
    public async Task<(JsonElement Resource, string? ETag)> GetWithETagAsync(
        string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(Describe(response.StatusCode, body, path));
        }

        return (JsonDocument.Parse(body).RootElement.Clone(), response.Headers.ETag?.ToString());
    }

    /// <summary>A <c>PUT</c> carrying the <c>If-Match</c> the resource was read with.</summary>
    /// <remarks>
    /// A <b>412</b> is a real case rather than a generic failure: somebody else changed the document between
    /// the read and this write, and the answer is to read it again — which for a bulk import means re-running
    /// it, since it is safe to. Named here so the message says that instead of leaving an operator to
    /// interpret a status code.
    /// </remarks>
    public async Task<JsonElement> PutWithETagAsync<TRequest>(
        string path, TRequest payload, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            throw new CliException(
                $"{path} was changed by somebody else between reading it and writing it. Re-run the import — "
                + "it is safe to, and it will skip everything already done.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(Describe(response.StatusCode, body, path));
        }

        return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
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
    /// A JSON <c>PUT</c> — the API's mutation verb (ADR: no PATCH; a PUT states the full intended value).
    /// </summary>
    /// <remarks>
    /// An EMPTY body is a normal answer here and not a failure: confirming a document version answers 204,
    /// and so does more than one mutation on this API. Parsing unconditionally would turn a success into a
    /// <c>JsonException</c> that reads like a protocol error.
    /// </remarks>
    public async Task<JsonElement> PutAsync<TRequest>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(path, payload, Json, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(Describe(response.StatusCode, body, path));
        }

        return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Sends bytes to an address OUTSIDE the API — a presigned object-storage upload.
    /// </summary>
    /// <remarks>
    /// Its own client, with NO Authorization header, and that is the point rather than an economy. The
    /// upload URL is signed and points at object storage on a possibly different host
    /// (<c>PublicServiceUrl</c>, ADR 0213); forwarding the API bearer token to it would hand our credential
    /// to another service, and some stores reject a request carrying both a signature and an Authorization
    /// header — a 403 that reads as a signing bug. The address is used EXACTLY as the server returned it.
    /// </remarks>
    public static async Task UploadAsync(Uri presignedUrl, byte[] payload, CancellationToken cancellationToken)
    {
        using var anonymous = new HttpClient();
        using var content = new ByteArrayContent(payload);
        using var response = await anonymous.PutAsync(presignedUrl, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(
                $"Object storage refused the upload ({(int)response.StatusCode}). The presigned URL may have "
                + "expired, or this host cannot reach the storage endpoint the installation advertises.");
        }
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
