using System.Text;

namespace SimplArchive.Api.Security;

/// <summary>
/// Composes the response security headers (ADR 0084, issue #844) and writes them on every response.
/// </summary>
/// <remarks>
/// <para>
/// The app sent none of these until now, and nothing was visibly broken — which is exactly why it went
/// unnoticed for so long. The absence of a browser-hardening header is invisible until the day something needs
/// it, and by then the header is not there.
/// </para>
/// <para>
/// <b>The policy is COMPOSED from configuration the deployment already has</b>, not restated. The browser talks
/// straight to object storage over presigned URLs — the API never proxies file bytes — so <c>connect-src</c> and
/// <c>img-src</c> must name that origin, and it is read from <c>ObjectStorage:PublicServiceUrl</c> rather than
/// asked for a second time. A CSP that omits it does not fail loudly: uploads and previews simply stop, in the
/// browser, with the server reporting nothing at all.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>The named CORS policy, registered only when a deployment configures origins.</summary>
    public const string CorsPolicyName = "SimplArchiveConfiguredOrigins";

    /// <summary>Where a server-rendered page finds this request's script nonce.</summary>
    public const string NonceKey = "SimplArchive.CspNonce";

    /// <summary>Stands in for the nonce in the composed policy until the per-request value is known.</summary>
    private const string NoncePlaceholder = "{nonce}";

    /// <summary>This request's nonce, for a page stamping its inline scripts.</summary>
    public static string? Nonce(HttpContext context) => context.Items[NonceKey] as string;

    /// <summary>Writes the headers, unless the response already carries one (an edge proxy set it).</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IConfiguration>()
            .GetSection(SecurityHeaderOptions.SectionName).Get<SecurityHeaderOptions>() ?? new SecurityHeaderOptions();

        if (!options.Enabled)
        {
            return app;
        }

        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var policy = options.ContentSecurityPolicy is { Length: > 0 } supplied
            ? supplied
            : ComposePolicy(StorageOrigin(configuration), options.AdditionalConnectSources);

        var hsts = options.EnableHsts
            ? $"max-age={(int)TimeSpan.FromDays(options.HstsMaxAgeDays).TotalSeconds}"
                + (options.HstsIncludeSubDomains ? "; includeSubDomains" : string.Empty)
            : null;

        return app.Use(async (context, next) =>
        {
            // A per-request nonce, created BEFORE anything renders so a server-rendered page can stamp it on
            // its inline scripts. The server-rendered surface — sign-in, MFA, passkeys — legitimately carries
            // inline script that interpolates a localised message, so extracting it to a file would drag the
            // localisation with it; a nonce is what CSP provides for exactly this case.
            //
            // Found the hard way (#844): a strict script-src with no nonce silently broke passkey registration
            // and passkey sign-in, and the only reason it did not ship that way is that two UI tests exercise
            // the WebAuthn flow in a real browser. Nothing server-side notices a blocked script.
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;

            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                // Never overwrite: a deployment that sets these at its edge owns them, and two policies are
                // intersected by the browser rather than merged — the stricter one wins and the app breaks.
                Set(headers, "Content-Security-Policy", policy.Replace(NoncePlaceholder, nonce, StringComparison.Ordinal));
                Set(headers, "X-Content-Type-Options", "nosniff");
                Set(headers, "Referrer-Policy", "strict-origin-when-cross-origin");

                // frame-ancestors in the CSP is the modern control and covers this; X-Frame-Options is kept for
                // browsers that honour only the older header. SAMEORIGIN, not DENY: the OIDC silent-renew flow
                // frames our own origin, and DENY would break token refresh in a way that presents as a random
                // sign-out.
                Set(headers, "X-Frame-Options", "SAMEORIGIN");
                Set(headers, "Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");

                if (hsts is not null && context.Request.IsHttps)
                {
                    Set(headers, "Strict-Transport-Security", hsts);
                }

                return Task.CompletedTask;
            });

            await next();
        });
    }

    /// <summary>The origin the browser reaches object storage at, or null when it is not configured.</summary>
    internal static string? StorageOrigin(IConfiguration configuration)
    {
        var configured = configuration["ObjectStorage:PublicServiceUrl"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = configuration["ObjectStorage:ServiceUrl"];
        }

        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    /// <summary>
    /// The default policy: everything from our own origin, plus the few exceptions the app genuinely needs.
    /// </summary>
    /// <remarks>
    /// Each exception is here because removing it breaks something specific, and is worth naming so the next
    /// reader does not "tighten" the policy back into an outage:
    /// <list type="bullet">
    /// <item><c>'wasm-unsafe-eval'</c> — Blazor WebAssembly compiles its runtime; without it the app does not
    /// start at all.</item>
    /// <item><c>style-src 'unsafe-inline'</c> — the component library injects the theme as inline styles at
    /// runtime, so there is no build-time hash to allow instead. Scripts do NOT need this: the one inline
    /// script that used to start Blazor was moved into a file precisely so <c>script-src</c> could stay
    /// strict.</item>
    /// <item><c>img-src data:</c> — a bearer-protected image (a profile photo) is fetched with the authenticated
    /// client and rendered as a data URL, because a plain <c>&lt;img src&gt;</c> sends no token.</item>
    /// <item><c>img-src blob:</c> and <c>worker-src blob:</c> — the PDF renderer draws pages to canvases and
    /// runs its own worker.</item>
    /// <item>the object-storage origin on <c>connect-src</c>/<c>img-src</c> — presigned upload, download and
    /// preview go straight there.</item>
    /// </list>
    /// </remarks>
    internal static string ComposePolicy(string? storageOrigin, string? additionalConnectSources)
    {
        var extra = new StringBuilder();
        if (storageOrigin is { Length: > 0 })
        {
            extra.Append(' ').Append(storageOrigin);
        }

        if (additionalConnectSources is { Length: > 0 } more)
        {
            extra.Append(' ').Append(more.Trim());
        }

        var remote = extra.ToString();

        return string.Join("; ",
        [
            "default-src 'self'",
            // 'unsafe-eval' is here because the client framework NEEDS it, which is worth stating plainly since
            // it is the one concession in this policy. Measured (#844): with every other directive permissive
            // and only this one strict, registering a passkey threw inside the renderer, reproducibly — while
            // sign-in, navigation and every HTTP call looked perfectly healthy. That is the shape of the whole
            // problem: a missing script-src source does not break the app, it breaks ONE interaction, and only
            // a browser test walking that interaction ever finds out.
            //
            // What is NOT conceded is inline script: no 'unsafe-inline', and the server-rendered pages carry a
            // per-request nonce instead. Blocking injected inline script is the part of CSP that actually
            // answers cross-site scripting, and it is intact.
            $"script-src 'self' 'wasm-unsafe-eval' 'unsafe-eval' 'nonce-{NoncePlaceholder}'",
            "style-src 'self' 'unsafe-inline'",
            "font-src 'self' data:",
            $"img-src 'self' data: blob:{remote}",
            $"connect-src 'self'{remote}",
            "worker-src 'self' blob:",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'self'",
        ]);
    }

    private static void Set(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name))
        {
            headers[name] = value;
        }
    }
}
