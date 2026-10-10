using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// The shared API client keeps its host settings and the signed-in content client in statics (ADR 0917), so the
// tests that set them run one at a time and put everything back.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiClientSettingsCollection
{
    public const string Name = "ApiClientSettings";
}

/// <summary>Captures the static settings on construction and restores them on dispose.</summary>
public abstract class ApiClientSettingsTest : IDisposable
{
    private readonly string _root = ApiClientSettings.ApiRootUrl;
    private readonly string _clientId = ApiClientSettings.ClientId;
    private readonly IEnvelopeOpener _opener = ApiClientSettings.EnvelopeOpener;
    private readonly ITokenSessionStore _sessions = ApiClientSettings.Sessions;
    private readonly ILogger _logger = ApiClientSettings.Logger;
    private readonly HttpClient? _authenticated = ApiCore.Authenticated;

    public virtual void Dispose()
    {
        ApiClientSettings.ApiRootUrl = _root;
        ApiClientSettings.ClientId = _clientId;
        ApiClientSettings.EnvelopeOpener = _opener;
        ApiClientSettings.Sessions = _sessions;
        ApiClientSettings.Logger = _logger;
        ApiCore.Authenticated = _authenticated;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A message handler answering from a function and recording what it was sent.</summary>
public sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Authorization, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return answer(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>A real HTTP server on a loopback port, for the code that builds its own handler stack.</summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, string, string, (int Status, string Body)> _answer;
    private readonly CancellationTokenSource _stop = new();

    public LoopbackServer(Func<string, string, string, (int Status, string Body)> answer)
    {
        _answer = answer;
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <summary>The server's root, with a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Method, path-and-query, body.</summary>
    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var path = context.Request.Url!.PathAndQuery;
            lock (Requests)
            {
                Requests.Add((context.Request.HttpMethod, path, body));
            }

            var (status, response) = _answer(context.Request.HttpMethod, path, body);
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}

/// <summary>An ILogger that keeps what it was told.</summary>
public sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
