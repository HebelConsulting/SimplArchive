namespace SimplArchive.Presentation;

/// <summary>
/// Whether a server profile (a name and an installation's address) may be saved: the rules the desktop's server
/// manager and the mobile app's server list must answer identically. Answers a localization KEY, so each client
/// says it in the user's language.
/// </summary>
public static class ServerProfileRules
{
    /// <summary>Null when the profile may be saved, otherwise the key of the reason it may not.</summary>
    /// <param name="otherNames">The names of the OTHER profiles: the one being edited is left out by the caller.</param>
    public static string? Problem(string name, string url, IEnumerable<string> otherNames)
    {
        if (name.Trim().Length == 0)
        {
            return "SmErrNameRequired";
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "SmErrUrlInvalid";
        }

        // The name is the profile's identity.
        return otherNames.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) ? "SmErrDuplicateName" : null;
    }
}
