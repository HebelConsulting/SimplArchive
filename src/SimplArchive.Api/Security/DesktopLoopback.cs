namespace SimplArchive.Api.Security;

/// <summary>
/// The desktop client's fixed loopback redirect (RFC 8252 "OAuth for Native Apps"), in ONE place because two
/// unrelated parts of the server have to agree about it: the OpenIddict client registration, which accepts the
/// redirect, and the content-security policy, which must not block the browser from reaching it.
/// </summary>
/// <remarks>
/// They did not agree, and the failure was invisible from both sides. `form-action 'self'` shipped in v0.10.0
/// and **Chrome enforces `form-action` across the whole redirect chain that follows a form submission** — so a
/// desktop sign-in POSTed the login form, the server answered its 302 to the authorization endpoint and then to
/// this address, and the browser refused the final hop. No error on the server, a valid registration, a green
/// suite, and the symptom was simply that nothing arrived at the loopback listener.
///
/// Nothing could have caught it: the desktop end-to-end suite is Chrome-free by design (ADR 0378) and drives the
/// api-client directly, while the browser suite exercises the web client, whose redirect target is same-origin.
/// So the guard is that both readers take the value from HERE — they can no longer disagree, which is the only
/// property a test can hold when no test can walk the path.
/// </remarks>
public static class DesktopLoopback
{
    /// <summary>The registered redirect URI — must match what the desktop client requests, exactly.</summary>
    public const string RedirectUri = "http://127.0.0.1:8765/callback";

    /// <summary>Its ORIGIN, which is what a content-security-policy source list names.</summary>
    public const string Origin = "http://127.0.0.1:8765";
}
