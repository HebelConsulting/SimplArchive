using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The signed-in account's own area (#443, ops tranche): the cached "me" resource and everything that
/// hangs off it — email, password, WebDAV password, MFA, passkeys, notification preferences, the avatar —
/// plus another user's identity card. Rides the shared authenticated <see cref="ApiCore"/>.
/// </summary>
public sealed class ProfileClient(ApiCore core)
{
    private readonly ApiCore _core = core;

    public sealed record UserCard(string DisplayName, string Email, bool IsActive, string? PhotoHref);
    // The caller's own "me" resource, cached for the same reason as the root. Everything about the signed-in
    // account hangs off it — password, photo, MFA, passkeys, WebDAV password, personal repository, notification
    // preferences — so this is the desktop's counterpart to the web client's MeHrefAsync (issue #416). Without
    // it every one of those was a composed /api/users/me/… path, which is thirteen private routes copied into a
    // second codebase.
    private IReadOnlyDictionary<string, string>? _meLinks;
    private string? _myEmail;
    private readonly SemaphoreSlim _meGate = new(1, 1);

    /// <summary>
    /// The caller's own email address, or <c>null</c> for a principal with no personal account.
    /// </summary>
    /// <remarks>
    /// Comes from the same "me" read the rels do, so a profile screen showing who you are signed in as costs no
    /// request of its own (#464).
    /// </remarks>
    public async Task<string?> MyEmailAsync(CancellationToken cancellationToken = default)
    {
        // Any rel will do: resolving one populates the whole document, email included.
        await MeHrefAsync("self", cancellationToken);
        return _myEmail;
    }

    /// <summary>
    /// The href for a rel on the caller's own "me" resource. Throws when it is not advertised.
    /// </summary>
    public async Task<string> MeHrefAsync(string rel, CancellationToken cancellationToken = default)
    {
        if (_meLinks is null)
        {
            // Resolve the root href BEFORE taking the gate: these are separate semaphores, but taking one while
            // holding the other is how the web client deadlocked its whole workbench (ADR 0543 notes), so keep
            // the acquisition order trivially safe by not nesting at all.
            var meHref = await _core.RootHrefAsync("me", cancellationToken);

            await _meGate.WaitAsync(cancellationToken);
            try
            {
                if (_meLinks is null)
                {
                    var me = await _core.Http.GetFromJsonAsync<JsonElement>(meHref, cancellationToken);
                    _meLinks = ApiCore.ParseLinks(me) ?? new Dictionary<string, string>();

                    // The email rides in the SAME response as the links (#464) — reading it here rather than
                    // adding a second call is ADR 0557's rule applied to a value, not an address: one read,
                    // everything it carried.
                    _myEmail = me.TryGetProperty("email", out var email) && email.ValueKind is JsonValueKind.String
                        ? email.GetString()
                        : null;
                }
            }
            finally
            {
                _meGate.Release();
            }
        }

        return _meLinks.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"The 'me' resource does not advertise the '{rel}' rel.");
    }

    /// <summary>
    /// The href for a root-level rel. Throws when the server does not advertise it — for the collections a screen
    /// is built around, a null would surface as an empty list ("you have no tags") rather than as a fault.
    /// </summary>
    // Fetch an author's identity card by FOLLOWING the href the message advertised (ADR 0544).
    public async Task<(UserCard Card, byte[]? Photo)?> GetUserCardAsync(string cardHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.GetAsync(cardHref, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var card = new UserCard(
            root.GetProperty("displayName").GetString() ?? "",
            root.GetProperty("email").GetString() ?? "",
            root.TryGetProperty("isActive", out var active) && active.GetBoolean(),
            ApiCore.RelHref(root, "photo"));

        // The photo rel is present only when one exists, so this never probes for a 404. The endpoint is
        // bearer-protected, so the bytes must come through the authenticated client.
        byte[]? photo = null;
        if (card.PhotoHref is { } photoHref)
        {
            try
            {
                photo = await _core.Http.GetByteArrayAsync(photoHref, cancellationToken);
            }
            catch (HttpRequestException)
            {
                photo = null;
            }
        }

        return (card, photo);
    }
    // ---- Passwords (ADR "User password management") -------------------------------------------------

    public async Task ChangeMyPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PutAsJsonAsync(await MeHrefAsync("changePassword", cancellationToken), new { currentPassword, newPassword }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("The current password is incorrect.");
        }

        response.EnsureSuccessStatusCode();
    }

    // WebDAV gateway (ADR "WebDAV gateway") — the app-specific WebDAV password + mount info.
    public sealed record WebDavStatus(bool Enabled, string Username, string Url, string? Password);

    public async Task<WebDavStatus> GetWebDavStatusAsync(CancellationToken cancellationToken = default) =>
        await _core.Http.GetFromJsonAsync<WebDavStatus>(await MeHrefAsync("webdavPassword", cancellationToken), cancellationToken) ?? new WebDavStatus(false, "", "", null);

    // Generate/regenerate — returns the plaintext password (shown once).
    public async Task<WebDavStatus> GenerateWebDavPasswordAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(await MeHrefAsync("webdavPassword", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebDavStatus>(cancellationToken))!;
    }

    public async Task RevokeWebDavPasswordAsync(CancellationToken cancellationToken = default) =>
        (await _core.Http.DeleteAsync(await MeHrefAsync("webdavPassword", cancellationToken), cancellationToken)).EnsureSuccessStatusCode();

    // ---- IMAP endpoint access (ADR "IMAP endpoint (read-only, first slice)", #562) ------------------

    // The status resource advertises generate/revoke/settings; the follows below take them from one read
    // (ADR 0557) rather than re-resolving the me resource per action.
    public sealed record ImapAccessInfo(bool Available, bool Enabled, string Username, string Host, int? Port, int? TlsPort, bool ShowAllDocuments, string? Password, IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    public async Task<ImapAccessInfo> GetImapAccessAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("imapAccess", cancellationToken), cancellationToken);
        return ParseImapAccess(json);
    }

    public async Task<ImapAccessInfo> GenerateImapPasswordAsync(ImapAccessInfo status, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(status.Href("generate") ?? throw new InvalidOperationException("The IMAP access resource advertised no 'generate' rel (ADR 0543)."), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseImapAccess(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task RevokeImapPasswordAsync(ImapAccessInfo status, CancellationToken cancellationToken = default) =>
        (await _core.Http.DeleteAsync(status.Href("revoke") ?? throw new InvalidOperationException("The IMAP access resource advertised no 'revoke' rel (ADR 0543)."), cancellationToken)).EnsureSuccessStatusCode();

    public async Task SetImapShowAllDocumentsAsync(ImapAccessInfo status, bool showAllDocuments, CancellationToken cancellationToken = default) =>
        (await _core.Http.PutAsJsonAsync(status.Href("settings") ?? throw new InvalidOperationException("The IMAP access resource advertised no 'settings' rel (ADR 0543)."), new { showAllDocuments }, cancellationToken)).EnsureSuccessStatusCode();

    private static ImapAccessInfo ParseImapAccess(JsonElement json) => new(
        json.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.True,
        json.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
        json.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
        json.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "",
        json.TryGetProperty("port", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt32() : null,
        json.TryGetProperty("tlsPort", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : null,
        json.TryGetProperty("showAllDocuments", out var sd) && sd.ValueKind == JsonValueKind.True,
        SimplArchiveApiClient.StrOrNull(json, "password"),
        ApiCore.ParseLinks(json));

    // ---- Two-factor authentication (ADR "MFA (interactive login, TOTP)") ----------------------------

    public sealed record MfaEnrollInfo(string Secret, string OtpauthUri, string QrDataUrl);

    // Starts enrollment: returns the secret + otpauth URI + QR data URL (the secret is stored server-side as
    // a pending, not-yet-active enrollment).
    public async Task<MfaEnrollInfo> EnrollMfaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(await MeHrefAsync("mfaEnroll", cancellationToken), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new MfaEnrollInfo(
            json.GetProperty("secret").GetString() ?? "",
            json.GetProperty("otpauthUri").GetString() ?? "",
            json.GetProperty("qrDataUrl").GetString() ?? "");
    }

    // Confirms enrollment with a code; returns the one-time recovery codes (shown once).
    public async Task<List<string>> EnableMfaAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(await MeHrefAsync("mfaEnable", cancellationToken), new { code }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("That authentication code isn't right.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
    }

    public async Task DisableMfaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(await MeHrefAsync("mfa", cancellationToken), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Passkeys (ADR "Desktop passkey management") ------------------------------------------------
    // List + remove are plain API calls the native app makes directly; registration needs a browser
    // attestation ceremony and is delegated to the system browser (see OidcLoopbackAuthenticator).

    // RemoveHref is the row's own `self` rel: a passkey addresses itself, so removing one follows a link the
    // list already carried instead of rebuilding /users/me/passkeys/{id} from an id (issue #416).
    public sealed record PasskeyInfo(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, string? RemoveHref = null);

    public async Task<List<PasskeyInfo>> GetPasskeysAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("passkeys", cancellationToken), cancellationToken);
        var list = new List<PasskeyInfo>();
        if (json.TryGetProperty("passkeys", out var passkeys))
        {
            foreach (var p in passkeys.EnumerateArray())
            {
                var links = ApiCore.ParseLinks(p);
                list.Add(new PasskeyInfo(
                    p.GetProperty("id").GetGuid(),
                    p.GetProperty("name").GetString() ?? "",
                    p.GetProperty("createdAt").GetDateTimeOffset(),
                    p.TryGetProperty("lastUsedAt", out var lu) && lu.ValueKind != JsonValueKind.Null ? lu.GetDateTimeOffset() : null,
                    links is not null && links.TryGetValue("self", out var removeHref) ? removeHref : null));
            }
        }

        return list;
    }

    /// <summary>Removes a passkey by the address its own row advertised (its `self` rel).</summary>
    public async Task RemovePasskeyAsync(string removeHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(removeHref, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ---- Notification email preferences (ADR "Notification preferences") -----------------------------

    public sealed record NotificationPreferenceInfo(int Type, string TypeName, bool EmailEnabled);

    public async Task<List<NotificationPreferenceInfo>> GetNotificationPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await MeHrefAsync("notificationPreferences", cancellationToken), cancellationToken);
        var list = new List<NotificationPreferenceInfo>();
        if (json.TryGetProperty("preferences", out var prefs))
        {
            foreach (var p in prefs.EnumerateArray())
            {
                list.Add(new NotificationPreferenceInfo(
                    p.GetProperty("type").GetInt32(),
                    p.GetProperty("typeName").GetString() ?? "",
                    p.GetProperty("emailEnabled").GetBoolean()));
            }
        }

        return list;
    }

    public async Task SetNotificationPreferencesAsync(IEnumerable<NotificationPreferenceInfo> preferences, CancellationToken cancellationToken = default)
    {
        var body = new { preferences = preferences.Select(p => new { type = p.Type, emailEnabled = p.EmailEnabled }) };
        using var response = await _core.Http.PutAsJsonAsync(await MeHrefAsync("notificationPreferences", cancellationToken), body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
    public async Task SetMyPhotoAsync(byte[] png, CancellationToken cancellationToken = default) =>
        await _core.PutPhotoAsync(await MeHrefAsync("photo", cancellationToken), png, cancellationToken);

    /// <summary>The caller's OWN avatar, at the address the `me` resource advertises for it.</summary>
    public async Task<byte[]?> GetMyPhotoAsync(CancellationToken cancellationToken = default) =>
        await _core.GetPhotoAsync(await MeHrefAsync("photo", cancellationToken), cancellationToken);


    /// <summary>Removes the caller's OWN avatar, at the address the `me` resource advertises.</summary>
    public Task DeleteMyPhotoAsync(CancellationToken cancellationToken = default) =>
        _core.DeletePhotoAsync(MeHrefAsync("photo", cancellationToken), cancellationToken);

    // Get-or-create the current user's personal repository (ADR "Per-user personal repository"). Returns null if
    // the caller has no personal space (e.g. a ServiceAccount → 403) so the tree still renders shared repositories.
    public async Task<Node?> GetPersonalRepositoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(await MeHrefAsync("personalRepository", cancellationToken), null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new Node(
            json.GetProperty("id").GetGuid(),
            json.GetProperty("name").GetString() ?? "Personal",
            json.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
            HasVersions: false,
            json.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
            HasReferences: false,
            // The resource advertises `children` — carry it, or the Personal tree node has no address to expand
            // by and Href() throws (ADR 0543). Hand-built Nodes are exactly where this is easy to forget, which
            // is what DesktopListingRelsTests now guards.
            Links: ApiCore.ParseLinks(json));
    }
}
