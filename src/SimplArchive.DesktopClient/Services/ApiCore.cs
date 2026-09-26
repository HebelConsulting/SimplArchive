using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The one authenticated HTTP core every per-area client shares (#443, tranche 1): the bearer-carrying
/// <see cref="Http"/>, the API-root link cache (<see cref="RootHrefAsync"/> — the root is the ONE URL a
/// client may know, ADR 0543; its rel set is structurally fixed, so caching it is allowed, ADR 0557), and
/// the shared wire helpers (<see cref="ParseLinks"/>, <see cref="ThrowIfProblemAsync"/>).
/// </summary>
/// <remarks>
/// Extracted from <c>SimplArchiveApiClient</c>, which now wraps this core and forwards to it — each area
/// tranche moves its methods onto a per-area client taking this core, and the god client shrinks
/// monotonically (the ceiling guard banks each step). Error translation stays the Problem-Details error-code
/// mapping: the code is the stable, language-neutral contract (issue #424).
/// </remarks>
public sealed class ApiCore
{
    /// <summary>A token-free client for content transfers — the address carries its own authorization.</summary>
    /// <remarks>
    /// No BaseAddress, ON PURPOSE: that is what stops this client's bearer token — which it does not have —
    /// or a relative path ever being sent somewhere the server did not address us to. Two shapes arrive here,
    /// and both authorize themselves without a header: a PRESIGNED object-storage URL (absolute, signed) and
    /// the at-rest encryption TOKEN DOOR (relative, `?t=` — ADR 0818). Resolve a content address with
    /// <see cref="ResolveContentUrl"/> before handing it to this client.
    /// </remarks>
    public static readonly HttpClient Anonymous = new();

    /// <summary>
    /// Turns a content address the server handed us into one this client can request: a RELATIVE href resolved
    /// against the installation, an absolute one returned untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both shapes are normal. A presigned object-storage URL is absolute; the at-rest encryption token door is
    /// <c>/api/encrypted-content?t=…</c> and deliberately relative, because the server hands one href to both
    /// clients and a browser resolves it against the page origin (ADR 0818). A desktop <c>HttpClient</c> with no
    /// base cannot, so the resolution has to happen here — in the one file ADR 0543 lets know the API root.
    /// </para>
    /// <para>
    /// Reported from the kiosk as <i>"Could not load '…': An invalid request URI was provided"</i>: in an
    /// encrypted tenant EVERY preview and every open-in-native-application failed on the desktop while the web
    /// client was fine. Nothing had regressed — the token door simply arrived later than the code that assumed
    /// every content address was absolute, and the assumption was written down as a comment rather than
    /// expressed as a type, so nothing re-read it.
    /// </para>
    /// <para>
    /// It stays anonymous either way: the door authenticates by its own token parameter, not by a header, so
    /// resolving the address changes where the request goes and not what it proves.
    /// </para>
    /// </remarks>
    public static Uri ResolveContentUrl(string url) =>
        // The scheme test is NOT redundant, and leaving it out is a bug that only shows on the platforms this
        // was reported from. On Unix, Uri.TryCreate("/api/encrypted-content?t=…", UriKind.Absolute, …) returns
        // TRUE — a leading-slash path is a valid absolute file:// URI — so "is it absolute?" answers yes for
        // exactly the address that needs resolving, and HttpClient then refuses it with "The 'file' scheme is
        // not supported". On Windows the same expression answers no and the naive form works, which is the
        // worst shape a platform difference can take: green on the developer's machine, broken on the user's.
        Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute
            : new Uri(new Uri(DesktopClientOptions.ApiBaseUrl), url);

    /// <summary>
    /// Fetches a content address the server handed us — bytes plus content type — resolving it first and
    /// sending no credentials.
    /// </summary>
    /// <remarks>
    /// THE one place the desktop reads content, so that resolving the address is a property of the codebase
    /// rather than something four call sites have to remember. It was not: the preview funnel, the
    /// open-in-native-application downloader and the page-thumbnail loader each held their own base-less
    /// <c>HttpClient</c>, so the token door broke three independent doors and the third one broke SILENTLY,
    /// inside a catch that answers "no thumbnails" (ADR 0575's trade).
    /// </remarks>
    public static async Task<(byte[] Bytes, string ContentType)> GetContentAsync(
        string url, CancellationToken cancellationToken = default)
    {
        using var response = await Anonymous.GetAsync(ResolveContentUrl(url), cancellationToken);
        response.EnsureSuccessStatusCode();

        // A strict tenant serves content only as a CMS envelope addressed to the reader (ADR 0828), so it is
        // opened HERE — the one funnel every read path in this client already passes through, which is why
        // rendering, opening in the real application, dragging out and thumbnailing all get it at once
        // instead of four times. An ordinary response is returned untouched.
        return EnvelopeOpener.Open(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    /// <summary>The bytes alone, for a caller that already knows what it is decoding.</summary>
    public static async Task<byte[]> GetContentBytesAsync(string url, CancellationToken cancellationToken = default) =>
        (await GetContentAsync(url, cancellationToken)).Bytes;

    private IReadOnlyDictionary<string, string>? _rootLinks;
    private readonly SemaphoreSlim _rootGate = new(1, 1);

    private readonly string _apiRootUrl;
    private readonly TokenSession.Holder _session;

    public ApiCore(string accessToken)
    {
        _apiRootUrl = DesktopClientOptions.ApiBaseUrl;

        // Honour the token this was CONSTRUCTED with. The login path records the full session first (with its
        // refresh token) and this leaves it alone; every other caller — impersonation, and every test that
        // builds a client from a bare token — gets a session seeded from what it passed.
        //
        // Without this the handler finds no session, sends no Authorization header, and every request 401s.
        // That is not hypothetical: it took 115 desktop tests down in one run, all of them reporting the same
        // 401 as if the server had rejected a credential rather than never being offered one.
        //
        // MaxValue, not "already expired": we do not know this token's lifetime and cannot renew it without a
        // refresh token, so claiming it needs renewal would make every request attempt a refresh it cannot do.
        // Adopt the recorded session when this client was built from the SAME token the login recorded — that
        // is the one case where a refresh token belongs to this client. Otherwise the client owns a private
        // session seeded from the token it was given, and does not renew.
        //
        // Deliberately NOT the shared store as the live source: it is keyed by SERVER, which is right for
        // persistence and wrong for identity — two clients for different users against one server would share
        // a slot and the second would silently become the first.
        var recorded = TokenSessions.Current.For(_apiRootUrl);
        var session = recorded is { RefreshToken.Length: > 0 }
            && string.Equals(recorded.AccessToken, accessToken, StringComparison.Ordinal)
                ? recorded
                // MaxValue, not "already expired": the lifetime is unknown and there is no refresh token, so
                // claiming it needs renewal would make every request attempt one it cannot perform.
                : new TokenSession(accessToken, null, DateTimeOffset.MaxValue);

        _session = new TokenSession.Holder(session);

        Http = new HttpClient(new RenewingAuthHandler(_apiRootUrl, _session, new HttpClientHandler()))
        {
            BaseAddress = new Uri(_apiRootUrl),
        };
        // The APP language, chosen at the logon window and applied before this client exists (ADR 0767):
        // module-localized texts are composed server-side from Accept-Language, and the sentence next to a
        // German UI must be German even on an English OS.
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        // Which zone this MACHINE is in (#1254), so a search for "the 6th" means the caller's calendar day
        // rather than the server's. Set once here rather than per request, for the same reason as the language:
        // a header a call site has to remember is one a new call site forgets, and the symptom — a
        // near-midnight document missing from exactly one day's results — is invisible from the code.
        //
        // The MACHINE zone, deliberately, and never the stored preference: this header answers "where am I",
        // and the server prefers a preference the user has set over whatever it says. Always an IANA id
        // (TimeZoneInfo.Local is a Windows id on Windows, which the server cannot resolve).
        Http.DefaultRequestHeaders.Add("X-Time-Zone", SimplArchive.Presentation.DisplayZone.IanaId(TimeZoneInfo.Local));
    }

    /// <summary>
    /// This client's bearer token — also the RFC 8693 subject_token for impersonation.
    /// </summary>
    /// <remarks>
    /// Read from the live session rather than captured at construction, so a caller that needs the raw token
    /// (impersonation) gets the one currently valid rather than the one this object was born with.
    /// </remarks>
    public string AccessToken => _session.Value?.AccessToken ?? string.Empty;

    /// <summary>The authenticated HttpClient every area client sends through.</summary>
    public HttpClient Http { get; }

    /// <summary>The API root's advertised href for <paramref name="rel"/> (cached after the first read).</summary>
    public async Task<string> RootHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_rootLinks is null)
        {
            await _rootGate.WaitAsync(cancellationToken);
            try
            {
                _rootLinks ??= await GetRootLinksAsync(cancellationToken);
            }
            finally
            {
                _rootGate.Release();
            }
        }

        return _rootLinks?.GetValueOrDefault(rel) is { } href
            ? href
            : throw new InvalidOperationException($"The API root does not advertise the '{rel}' rel.");
    }

    /// <summary>The API root's link relations, uncached. Note "api" carries no slash — not a composed path.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetRootLinksAsync(CancellationToken cancellationToken = default)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        using var response = await Http.GetAsync("api", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // NEVER an empty link set on a failed read (#1077). An empty set is indistinguishable from a root
            // that advertises nothing, so RootHrefAsync then blamed the CONTRACT — "the API root does not
            // advertise the 'repositories' rel" — for what was a 500. A wrong diagnosis is worse than a plain
            // failure (the serving-something-else rule): it sent the reader looking at hypermedia while the
            // server was broken. Found live when a corrupted module assembly made GET /api throw and the
            // desktop answered a SUCCESSFUL sign-in with a silent "Not logged in.".
            //
            // HttpRequestException carrying the status: AppExceptions classifies it as a connectivity failure,
            // which is the recoverable modal that retries — the honest offer when the server is answering badly.
            throw new HttpRequestException(
                $"The API root answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        // A 200 is not yet an API root (#1077). An address pointing at the SPA host — a mistyped server entry,
        // the commonest way to get here — answers 200 with index.html, and parsing that raised a raw
        // JsonReaderException ("'<' is an invalid start of a value") from deep inside the client. Same rule as
        // the branch above: say what is wrong with the ADDRESS, not what a parser found in byte 0.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (JsonException e)
        {
            throw new HttpRequestException(
                $"The address answered {(int)response.StatusCode} but not with an API root (its body is not JSON).", e);
        }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("links", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in items.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var rel) && rel.GetString() is { Length: > 0 } name
                        && link.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } value)
                    {
                        // Trimmed here rather than at each caller: the href is absolute-from-root ("/api/tags") and
                        // this HttpClient has a BaseAddress, so a leading slash escapes any path prefix it carries.
                        // The second copy of this method trimmed and this one did not — harmless only while the
                        // base address has no prefix, which is exactly the kind of agreement two copies stop keeping.
                        links[name] = value.TrimStart('/');
                    }
                }
            }
        }

        // A JSON body with no links is not an API root either — the same wrong-diagnosis trap one step later.
        if (links.Count == 0)
        {
            throw new HttpRequestException("The address answered with JSON that carries no API-root links.");
        }

        return links;
    }

    /// <summary>
    /// Loads every page of a cursor-paginated listing (ADR 0207): follows the envelope's `next` rel until
    /// exhausted, parsing <paramref name="arrayProperty"/>'s items with <paramref name="parse"/>.
    /// </summary>
    public async Task<List<T>> LoadPagedAsync<T>(string url, string arrayProperty, Func<JsonElement, T> parse, CancellationToken cancellationToken,
        Action<JsonElement>? onPage = null)
    {
        var items = new List<T>();
        string? next = url;

        while (next is not null)
        {
            var page = await Http.GetFromJsonAsync<JsonElement>(next, cancellationToken);
            onPage?.Invoke(page);
            if (page.TryGetProperty(arrayProperty, out var array))
            {
                items.AddRange(array.EnumerateArray().Select(parse));
            }

            next = FindLink(page, "next");
        }

        return items;
    }

    /// <summary>The resource's advertised href for <paramref name="rel"/>, or null.</summary>
    public static string? FindLink(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links))
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.GetProperty("rel").GetString() == rel)
            {
                return link.GetProperty("href").GetString();
            }
        }

        return null;
    }

    /// <summary>The row's advertised links, or null when it carries none.</summary>
    public static LinkMap? ParseLinks(JsonElement item) =>
        SimplArchiveApiClient.ParseLinks(item);

    /// <summary>Maps a Problem-Details refusal to a localized <see cref="ApiActionException"/>.</summary>
    public static Task ThrowIfProblemAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken) =>
        SimplArchiveApiClient.ThrowIfProblemAsync(response, fallback, cancellationToken);
    public async Task<byte[]?> GetPhotoAsync(string photoHref, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(photoHref, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeletePhotoAsync(Task<string> photoHref, CancellationToken cancellationToken)
    {
        using var response = await Http.DeleteAsync(await photoHref, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>PUTs a PNG to an advertised photo href, translating the refusals a caller can act on.</summary>
    public async Task PutPhotoAsync(string url, byte[] png, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await Http.PutAsync(url, content, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to change this photo.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("That image could not be used as a profile photo.");
        }

        response.EnsureSuccessStatusCode();
    }



    // The href a resource advertises for a rel, or null when it doesn't offer one. A missing rel is meaningful —
    // it means "not available here" — so callers branch on null rather than composing a URL (ADR 0543).
    // internal: IntrayApi follows rels too, since the intray calls moved there (#443 direction).
    /// <summary>
    /// The problem document's errorCode, or null when the body is not a problem (a proxy error page).
    /// The contract the clients localize from (issue #424) — never the English `detail`. Parsed once,
    /// branched from the parse (the read-the-problem-body-once lesson).
    /// </summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken = default) =>
        (await ProblemAsync(response, cancellationToken)).Code;

    /// <summary>The problem's code, plus the module-localization pair (ABI 0.10, core ADR 0767): a problem
    /// carrying "module" has a detail the module's catalog composed for the request culture — the one
    /// server text the no-server-detail rule licenses. One parse, all three facts.</summary>
    public static async Task<(string? Code, string? Module, string? Detail)> ProblemAsync(
        HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return (
                problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null,
                problem.TryGetProperty("module", out var module) ? module.GetString() : null,
                problem.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Sends AT a rel carried on an already-parsed ROW — the address and the method both come from the link
    /// the server advertised, so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// This is the overload the burn-down needed. The <see cref="JsonElement"/> one below can only serve a
    /// caller that still holds the response, and the sites that actually name verbs do not:
    /// <c>AddLegalHoldItemAsync(LegalHoldInfo hold, …)</c> takes a row, asks it for an href and then names
    /// <c>PostAsJsonAsync</c> — by then the JSON is gone. Carrying the method on the row (see
    /// <see cref="LinkMap"/>) is what makes those sites reachable at all (#1192).
    /// </remarks>
    public async Task<HttpResponseMessage> SendRelAsync(
        LinkMap? links, string rel, object? body = null, CancellationToken cancellationToken = default)
    {
        if (links?.Href(rel) is not { } href)
        {
            throw new InvalidOperationException($"The '{rel}' rel was not advertised (ADR 0543).");
        }

        using var request = new HttpRequestMessage(Verb(links.Method(rel), rel), href);
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        return await Http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends AT a rel on a resource the client just read: the address and the METHOD both come from the link
    /// the server advertised, so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// ADR 0543 makes rel names the compatibility surface, and #1173 proved the address half — fourteen routes
    /// moved and no client learned a new one. The method half was never true: <see cref="ParseLinks"/> returns
    /// rel → href and throws the METHOD away entirely, so every call site named the verb itself and a route
    /// that kept its rel and changed its verb still broke this client.
    ///
    /// A caller of this cannot name a verb, which is the point (#1192).
    /// </remarks>
    public async Task<HttpResponseMessage> SendRelAsync(
        JsonElement resource, string rel, object? body = null, CancellationToken cancellationToken = default)
    {
        var (href, method) = RelLink(resource, rel);
        if (href is null)
        {
            throw new InvalidOperationException($"The '{rel}' rel was not advertised (ADR 0543).");
        }

        using var request = new HttpRequestMessage(Verb(method, rel), href);
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        return await Http.SendAsync(request, cancellationToken);
    }

    // The href AND the method for one rel. ParseLinks deliberately keeps its rel → href shape: 77 call sites
    // read it for navigation and do not need the verb, and widening that return type would be a large change
    // for no gain at those sites.
    public static (string? Href, string? Method) RelLink(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out var r) && r.GetString() == rel
                && link.TryGetProperty("href", out var h) && h.GetString() is { Length: > 0 } href)
            {
                return (href.TrimStart('/'), link.TryGetProperty("method", out var m) ? m.GetString() : null);
            }
        }

        return (null, null);
    }

    // An advertised method the client does not recognise is a refusal, not a default: guessing POST is how a
    // client comes to write where the server meant it to read.
    private static HttpMethod Verb(string? method, string rel) => method?.ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "PUT" => HttpMethod.Put,
        "POST" => HttpMethod.Post,
        "DELETE" => HttpMethod.Delete,
        "HEAD" => HttpMethod.Head,
        _ => throw new InvalidOperationException($"The '{rel}' rel advertised no usable method ('{method}')."),
    };

    public static string? RelHref(JsonElement resource, string rel)
    {
        if (!resource.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out var r) && r.GetString() == rel
                && link.TryGetProperty("href", out var h) && h.GetString() is { Length: > 0 } href)
            {
                return href.TrimStart('/');
            }
        }

        return null;
    }

    // Follows a rel off a resource the client just READ or just CREATED — the case where the address is already
    // in hand and only needs picking up, as opposed to DocumentRelAsync's "I hold an id, fetch the resource".
    public static string RequireRel(JsonElement resource, string rel, string what) =>
        ApiCore.ParseLinks(resource) is { } links && links.Href(rel) is { } href
            ? href
            : throw new InvalidOperationException($"{what} advertised no '{rel}' rel (ADR 0543).");

    public static string RequireHref(IAdvertisesLinks row, string rel) =>
        row.Href(rel)
        ?? throw new InvalidOperationException($"The row '{row.Name}' advertised no '{rel}' rel (ADR 0543/0555).");


}
