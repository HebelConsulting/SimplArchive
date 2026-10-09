namespace SimplArchive.Api.Security;

/// <summary>
/// The mobile app's redirect (ADR 0916): a private-use URI scheme (RFC 8252 §7.1), the reverse-DNS form of the app id,
/// in ONE place for the reason <see cref="DesktopLoopback"/> gives. The client registration accepts it, and the
/// content-security policy's <c>form-action</c> must let the browser reach it, or sign-in silently never returns to the app.
/// </summary>
public static class MobileRedirect
{
    /// <summary>The registered redirect URI, which must match what the app requests exactly.</summary>
    public const string RedirectUri = "dev.simplarchive.mobile:/oauth2redirect";

    /// <summary>Its scheme as a content-security-policy scheme source.</summary>
    public const string SchemeSource = "dev.simplarchive.mobile:";
}
