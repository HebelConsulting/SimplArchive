using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The administration area's client (#443, tranche 4): users/groups with rights and membership, service
/// accounts, tenant settings, and the sensitivity-label catalog, over the shared authenticated
/// <see cref="ApiCore"/>. Reached as <c>api.Admin</c>. Every mutation follows a rel the principal's or
/// account's own row advertised (ADR 0543/0555); document-scoped sensitivity stays with the documents area.
/// </summary>
public sealed class AdminClient(ApiCore core)
{
    private readonly ApiCore _core = core;


    // Tenant-wide system-level rights, mirroring the User/Group columns (ADR "Users & groups administration
    // tab"). Backs the rights matrix on the Users & groups tab.
    public sealed record SystemRightsData(
        bool IsTenantAdmin, bool CanImpersonate, bool CanOverrideCheckout, bool CanLegalHold,
        bool CanManageClassification, bool CanResetMfa, bool CanManageRepositories, bool CanManageMasks,
        bool CanManageServiceAccounts, bool CanManageUsers, bool CanViewAuditLog, bool CanExport, bool CanImport,
        // Tenant-wide intray triage (ADR 0532). Defaulted so existing 13-bool construction sites keep compiling.
        bool CanManageIntrays = false,
        // Share a document with someone who has no account (ADR 0546). Defaulted for the same reason.
        bool CanCreateExternalLink = false,
        // See + read where no grant exists, and nothing else (ADR 0670). Defaulted for the same reason.
        bool CanAccessWithoutGrant = false,
        // Write a Mailbox's address list, delete/restore a mailbox (#703). Defaulted for the same reason.
        bool CanManageMailRouting = false,
        // Data-classification clearance (ADR "Sensitivity clearance enforcement"). Defaulted so existing
        // construction sites (e.g. a copied-rights bundle) keep compiling.
        int ClearanceRank = 0);

    // A user or group in the combined admin list (ADR "Users & groups administration tab"). IsActive is
    // meaningful only for a user (a group has no active/inactive concept).
    // Links are the row's own advertised addresses (ADR 0543/0555): rights, photo, reset-password, reset-mfa,
    // deactivate for a user; rights, members, delete for a group. The client's methods take this row and follow
    // one of them, instead of rebuilding /users/{id}/… and /groups/{id}/… paths from an id.
    public sealed record PrincipalInfo(bool IsGroup, Guid Id, string Name, bool IsActive, SystemRightsData Rights, bool MfaEnabled = false, bool ImapShowAllDocuments = false,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // A machine-to-machine service account (ADR 0203/0534). ClientId is the OAuth client_id; the client_secret is
    // only ever returned once on create/rotate (see NewSecret) and is never carried on a list/read.
    // The row's actions disable from the SERVER's answer rather than from IsActive re-derived here (#416).
    // That answer used to be the absence of edit/revoke; it is now CanManage, because those two rels sat on
    // `self`'s own address and said nothing the method did not carry (ADR 0719). `rotate-secret` is still a
    // rel — a different address — and is still absent on a revoked account.
    public sealed record ServiceAccountInfo(Guid Id, string Name, string ClientId, bool IsActive, bool CanManage,
        bool CanManageRepositories, bool CanManageMasks, bool CanManageServiceAccounts, bool CanImport, bool CanExport,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    // The one-time client_id + client_secret shown after create/rotate — never retrievable again.
    public sealed record ServiceAccountSecret(string ClientId, string ClientSecret);

    // SelfHref / RetireHref / UnretireHref are the addresses the catalog row advertised. Exactly one of the last
    // two is present, and which one it is expresses the label's state (ADR 0543, issue #416).
    // ---- Mail domains (#667, ADR 0692) ----------------------------------------------------------------

    /// <param name="ChallengeName">Where to publish the TXT record; null once there is nothing left to prove.</param>
    /// <param name="ChallengeValue">What to publish there. Null for a domain the configuration declared.</param>
    /// <param name="VerifyHref">Advertised only while unverified — its absence IS "nothing to verify".</param>
    public sealed record MailDomainInfo(
        Guid Id, string Domain, bool Verified, string? ChallengeName, string? ChallengeValue,
        string? VerifyHref = null, string? RemoveHref = null);

    /// <param name="AddHref">Advertised only to a caller who may add one — absent means no button.</param>
    public sealed record MailDomainList(IReadOnlyList<MailDomainInfo> Domains, bool CanManage, string? AddHref);

    /// <summary>The tenant's mail domains, with what each one still needs.</summary>
    public async Task<MailDomainList> GetMailDomainsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(
            await _core.RootHrefAsync("mailDomains", cancellationToken), cancellationToken);

        var items = new List<MailDomainInfo>();
        if (json.TryGetProperty("domains", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in arr.EnumerateArray())
            {
                var links = ApiCore.ParseLinks(d) ?? new Dictionary<string, string>();
                items.Add(new MailDomainInfo(
                    d.GetProperty("id").GetGuid(),
                    d.GetProperty("domain").GetString() ?? string.Empty,
                    d.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                    Text(d, "challengeName"),
                    Text(d, "challengeValue"),
                    links.GetValueOrDefault("verify"),
                    links.GetValueOrDefault("remove")));
            }
        }

        return new MailDomainList(
            items,
            json.TryGetProperty("canManage", out var cm) && cm.GetBoolean(),
            (ApiCore.ParseLinks(json) ?? new Dictionary<string, string>()).GetValueOrDefault("add"));
    }

    /// <summary>Claims a domain at the address the collection advertised. Unverified until it is proven.</summary>
    public async Task AddMailDomainAsync(string addHref, string domain, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(addHref, new { domain }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The mail domain could not be added.", cancellationToken);
    }

    /// <summary>Asks the server to look for the challenge now. Repeatable — DNS takes its time.</summary>
    public async Task VerifyMailDomainAsync(string verifyHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(verifyHref, null, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The mail domain could not be verified.", cancellationToken);
    }

    public async Task RemoveMailDomainAsync(string removeHref, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(removeHref, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The mail domain could not be removed.", cancellationToken);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public sealed record SensitivityLabelInfo(Guid Id, string Name, int Rank, string? Color, bool Watermark, bool Retired,
        string? SelfHref = null, string? RetireHref = null, string? UnretireHref = null);
    public sealed record SensitivityLabelCatalog(IReadOnlyList<SensitivityLabelInfo> Items, bool CanManage);

    // The tenant's configurable label catalog (for the picker + admin).
    public async Task<SensitivityLabelCatalog> GetSensitivityLabelsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("sensitivityLabels", cancellationToken), cancellationToken);
        var items = new List<SensitivityLabelInfo>();
        if (json.TryGetProperty("labels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in arr.EnumerateArray())
            {
                var links = ApiCore.ParseLinks(l) ?? new Dictionary<string, string>();
                items.Add(new SensitivityLabelInfo(
                    l.GetProperty("id").GetGuid(),
                    l.GetProperty("name").GetString() ?? "",
                    l.TryGetProperty("rank", out var r) ? r.GetInt32() : 0,
                    l.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                    l.TryGetProperty("watermark", out var w) && w.ValueKind == JsonValueKind.True,
                    l.TryGetProperty("retired", out var rt) && rt.ValueKind == JsonValueKind.True,
                    links.GetValueOrDefault("self"),
                    // Exactly one of these is advertised, and which one IS the label's state — the client no
                    // longer decides "retire or un-retire?" from the Retired flag (issue #416).
                    links.GetValueOrDefault("retire"),
                    links.GetValueOrDefault("unretire")));
            }
        }

        return new SensitivityLabelCatalog(items, json.TryGetProperty("canManage", out var cm) && cm.GetBoolean());
    }

    public async Task CreateSensitivityLabelAsync(string name, int rank, string? color, bool watermark, CancellationToken cancellationToken = default)
    {
        var resp = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("sensitivityLabels", cancellationToken), new { name, rank, color, watermark }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await SimplArchiveApiClient.ErrorMessageAsync(resp, "Could not add the label."));
    }

    /// <summary>Updates a label at the address its own catalog row advertised (`self`).</summary>
    public async Task UpdateSensitivityLabelAsync(string selfHref, string name, int rank, string? color, bool watermark, CancellationToken cancellationToken = default)
    {
        var resp = await _core.Http.PutAsJsonAsync(selfHref, new { name, rank, color, watermark }, cancellationToken);
        if (!resp.IsSuccessStatusCode) throw new ApiActionException(await SimplArchiveApiClient.ErrorMessageAsync(resp, "Could not update the label."));
    }

    public async Task RetireSensitivityLabelAsync(string retireHref, CancellationToken cancellationToken = default) =>
        (await _core.Http.DeleteAsync(retireHref, cancellationToken)).EnsureSuccessStatusCode();

    public async Task UnretireSensitivityLabelAsync(string unretireHref, CancellationToken cancellationToken = default) =>
        (await _core.Http.PostAsync(unretireHref, null, cancellationToken)).EnsureSuccessStatusCode();

    // ---- Tenant-admin settings (ADR "Tenant-admin settings tab") -----------------------------------

    public sealed record TenantSettingsInfo(Guid Id, string Name, string Status, DateTimeOffset CreatedAt, string DefaultOcrLanguages, int AuditRetentionDays, int CheckoutTtlDays, int CheckoutWarningDays, int WormLockMode, bool RequireMfa, bool AllowPasskeyLogin, bool RequireDispositionReview, bool RestrictTagsToCatalog, bool EnforceClearance, bool ImapShowAllDocumentsDefault, bool AllowExternalLinks, int ExternalLinkMaxDays, int ExternalLinkDefaultAccesses, bool ShowExternalLinkUrl, long? StorageQuotaBytes, long StorageUsedBytes, int IncompleteUploadCleanupDays, string? AuditWebhookUrl, bool AuditWebhookConfigured, int AuditWebhookConsecutiveFailures, DateTimeOffset? AuditWebhookLastSuccessAt, DateTimeOffset? AuditWebhookLastFailureAt, DateTimeOffset? AuditWebhookNextAttemptAt, string? AuditWebhookLastError,
        IReadOnlyDictionary<string, string>? Links = null);

    public async Task<TenantSettingsInfo> GetTenantSettingsAsync(CancellationToken cancellationToken = default)
    {
        var j = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("tenantSettings", cancellationToken), cancellationToken);
        return ParseTenantSettings(j);
    }

    // Rebuilds the tenant's used-storage counter from the actual stored blobs (ADR "Per-tenant storage quota").
    public async Task<TenantSettingsInfo> RecomputeStorageAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(await TenantSettingsRelAsync("recompute-storage", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to recompute storage usage.");
        }

        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseTenantSettings(j);
    }

    // NOTE: this PUT is a FULL REPLACEMENT — a field left out of the payload is set to its DTO default, not left
    // alone. The external-link settings are therefore REQUIRED parameters rather than optional ones: when they
    // were simply missing here, a desktop admin saving any unrelated tenant setting silently switched external
    // links off AND set both caps to 0. An optional default would recreate exactly that bug at the next caller.
    // ONE generic per-group save (#530 tranche 10, ADR "Per-group tenant settings"): the caller passes the
    // already-read settings (whose links carry the writable sub-resources) plus the group's rel suffix and its
    // payload. Follows the advertised settings-<group> rel (ADR 0543) — a missing rel means "not offered".
    public async Task<TenantSettingsInfo> SaveTenantSettingsGroupAsync(TenantSettingsInfo settings, string group, object body, CancellationToken cancellationToken = default)
    {
        var href = settings.Links?.GetValueOrDefault($"settings-{group}")
            ?? throw new ApiActionException("The server offered no way to edit these settings.");
        using var response = await _core.Http.PutAsJsonAsync(href, body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("Another active tenant already uses this name.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("Check the entered values (name, OCR languages, retention, webhook URL/secret).");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage tenant settings.");
        }

        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ParseTenantSettings(j);
    }

    // ---- Users & groups administration (ADR "Users & groups administration tab") --------------------

    public async Task<List<PrincipalInfo>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync(await _core.RootHrefAsync("users", cancellationToken), "users", ParseUser, cancellationToken);

    public async Task<List<PrincipalInfo>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync(await _core.RootHrefAsync("groups", cancellationToken), "groups", ParseGroup, cancellationToken);

    /// <summary>
    /// Creates a user and returns the created ROW — not its id. The create response is the resource, rels
    /// included, so a caller that goes on to act on what it created already holds the addresses (ADR 0555).
    /// </summary>
    public async Task<PrincipalInfo> CreateUserAsync(string email, string displayName, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("users", cancellationToken), new { email, displayName }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("A user with this email already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage users.");
        }

        response.EnsureSuccessStatusCode();
        return ParseUser(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    public async Task<PrincipalInfo> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("groups", cancellationToken), new { name }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A group named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage groups.");
        }

        response.EnsureSuccessStatusCode();
        return ParseGroup(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    private static string RequireHref(ServiceAccountInfo account, string rel) =>
        account.Href(rel)
        ?? throw new InvalidOperationException($"The service account advertised no '{rel}' rel — a revoked account offers none (ADR 0543/0555).");

    private static string RequireHref(PrincipalInfo principal, string rel) =>
        principal.Href(rel)
        ?? throw new InvalidOperationException($"The {(principal.IsGroup ? "group" : "user")} row advertised no '{rel}' rel (ADR 0543/0555).");

    /// <summary>Sets a principal's system rights at the address its own row advertised.</summary>
    public Task SetRightsAsync(PrincipalInfo principal, SystemRightsData rights, CancellationToken cancellationToken = default) =>
        SetRightsCoreAsync(RequireHref(principal, "rights"), rights, cancellationToken);

    // Deactivates a user (reversible on the server; the row stays, marked inactive).
    // Deactivates a user. If they still hold pending review tasks, the server refuses (409
    // REVIEWER_HAS_PENDING_REVIEWS) unless reassignReviewsTo hands them to a replacement reviewer (ADR
    // "Workflow review reassignment") — surfaced as ReviewerHasPendingReviewsException so the caller can prompt.
    public async Task DeleteUserAsync(PrincipalInfo user, Guid? reassignReviewsTo = null, CancellationToken cancellationToken = default)
    {
        // The reassignment is a QUERY on the advertised address, not a path this client invents.
        var deactivateHref = RequireHref(user, "deactivate");
        var url = reassignReviewsTo is { } r ? $"{deactivateHref}?reassignReviewsTo={r}" : deactivateHref;
        using var response = await _core.Http.DeleteAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage users.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict && await SimplArchiveApiClient.ErrorCodeAsync(response, cancellationToken) == "REVIEWER_HAS_PENDING_REVIEWS")
        {
            throw new ReviewerHasPendingReviewsException("This user still holds pending review tasks.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Deletes a group (409 if it still has child groups or members).
    public async Task DeleteGroupAsync(PrincipalInfo group, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(RequireHref(group, "delete"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException("The group still has child groups or members.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage groups.");
        }

        response.EnsureSuccessStatusCode();
    }

    // ---- Service accounts (ADR 0203/0534) -----------------------------------------------------------

    public async Task<List<ServiceAccountInfo>> GetServiceAccountsAsync(CancellationToken cancellationToken = default) =>
        await _core.LoadPagedAsync(await _core.RootHrefAsync("serviceAccounts", cancellationToken), "serviceAccounts", ParseServiceAccount, cancellationToken);

    // Create a service account with its rights; returns the one-time client_id + client_secret (shown once).
    public async Task<ServiceAccountSecret> CreateServiceAccountAsync(string name, SystemRightsData rights, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(await _core.RootHrefAsync("serviceAccounts", cancellationToken), ToServiceAccountBody(name, rights), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A service account named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You can only grant rights you hold yourself.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new ServiceAccountSecret(json.GetProperty("clientId").GetString() ?? "", json.GetProperty("clientSecret").GetString() ?? "");
    }

    // Edit an existing account's name + rights (PUT, ADR 0534) — escalation-capped server-side like create.
    public async Task UpdateServiceAccountAsync(ServiceAccountInfo account, string name, SystemRightsData rights, CancellationToken cancellationToken = default)
    {
        // PUT at the account's own address (ADR 0719); whether it may be edited is CanManage's answer.
        using var response = await _core.Http.PutAsJsonAsync(RequireHref(account, "self"), ToServiceAccountBody(name, rights), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ApiActionException($"A service account named '{name}' already exists.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You can only grant rights you hold yourself.");
        }

        response.EnsureSuccessStatusCode();
    }

    // Rotate the secret — mints a new client_secret and invalidates the old one; returns the one-time secret.
    public async Task<ServiceAccountSecret> RotateServiceAccountSecretAsync(ServiceAccountInfo account, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(account, "rotate-secret"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage service accounts.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new ServiceAccountSecret(json.GetProperty("clientId").GetString() ?? "", json.GetProperty("clientSecret").GetString() ?? "");
    }

    // Revoke — one-way, sets IsActive = false; the credentials stop working immediately.
    public async Task RevokeServiceAccountAsync(ServiceAccountInfo account, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.DeleteAsync(RequireHref(account, "self"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage service accounts.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static ServiceAccountInfo ParseServiceAccount(JsonElement e)
    {
        bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new ServiceAccountInfo(
            e.GetProperty("id").GetGuid(),
            e.GetProperty("name").GetString() ?? "",
            e.TryGetProperty("clientId", out var c) ? c.GetString() ?? "" : "",
            !e.TryGetProperty("isActive", out var a) || a.ValueKind == JsonValueKind.True,
            B("canManage"),
            B("canManageRepositories"), B("canManageMasks"), B("canManageServiceAccounts"), B("canImport"), B("canExport"),
            ApiCore.ParseLinks(e));
    }

    // Admin reset — returns the generated password (shown once).
    public async Task<string> ResetUserPasswordAsync(PrincipalInfo user, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(user, "reset-password"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to reset passwords.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("password").GetString() ?? "";
    }

    // Admin reset — disables a locked-out user's two-factor.
    public async Task ResetUserMfaAsync(PrincipalInfo user, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(RequireHref(user, "reset-mfa"), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to reset two-factor authentication.");
        }

        response.EnsureSuccessStatusCode();
    }

    // ---- Group membership (ADR "Group membership editing") ------------------------------------------

    public Task<List<UserOptionInfo>> GetGroupMembersAsync(PrincipalInfo group, CancellationToken cancellationToken = default) =>
        _core.LoadPagedAsync(RequireHref(group, "members"), "members", SimplArchiveApiClient.ParseMember, cancellationToken);

    // The API takes the member in the BODY of a POST to the collection now, so the group row's `members`
    // address serves every add — the chosen user travels as data, not as a path segment (issue #416).
    public async Task AddGroupMemberAsync(PrincipalInfo group, Guid userId, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsJsonAsync(RequireHref(group, "members"), new { userId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to manage members.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveGroupMemberAsync(UserOptionInfo member, CancellationToken cancellationToken = default)
    {
        var removeHref = member.RemoveHref
            ?? throw new InvalidOperationException("The member row advertised no 'remove' rel (ADR 0543/0555).");
        using var response = await _core.Http.DeleteAsync(removeHref, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    // ---- Profile photo (ADR "User profile photo") ---------------------------------------------------

    public Task SetUserPhotoAsync(PrincipalInfo user, byte[] png, CancellationToken cancellationToken = default) =>
        _core.PutPhotoAsync(RequireHref(user, "photo"), png, cancellationToken);


    private static TenantSettingsInfo ParseTenantSettings(JsonElement j) => new(
        j.GetProperty("id").GetGuid(),
        j.GetProperty("name").GetString() ?? "",
        j.GetProperty("status").GetString() ?? "",
        j.GetProperty("createdAt").GetDateTimeOffset(),
        j.GetProperty("defaultOcrLanguages").GetString() ?? "",
        j.TryGetProperty("auditRetentionDays", out var r) ? r.GetInt32() : 0,
        j.TryGetProperty("checkoutTtlDays", out var c) ? c.GetInt32() : 0,
        j.TryGetProperty("checkoutWarningDays", out var cw) ? cw.GetInt32() : 1,
        j.TryGetProperty("wormLockMode", out var w) ? w.GetInt32() : 0,
        j.TryGetProperty("requireMfa", out var m) && m.ValueKind == JsonValueKind.True,
        j.TryGetProperty("allowPasskeyLogin", out var pk) && pk.ValueKind == JsonValueKind.True,
        j.TryGetProperty("requireDispositionReview", out var dr) && dr.ValueKind == JsonValueKind.True,
        j.TryGetProperty("restrictTagsToCatalog", out var rt) && rt.ValueKind == JsonValueKind.True,
        j.TryGetProperty("enforceClearance", out var ec) && ec.ValueKind == JsonValueKind.True,
        j.TryGetProperty("imapShowAllDocumentsDefault", out var im) && im.ValueKind == JsonValueKind.True,
        j.TryGetProperty("allowExternalLinks", out var xl) && xl.ValueKind == JsonValueKind.True,
        j.TryGetProperty("externalLinkMaxDays", out var xd) ? xd.GetInt32() : 180,
        j.TryGetProperty("externalLinkDefaultAccesses", out var xa) ? xa.GetInt32() : 5,
        j.TryGetProperty("showExternalLinkUrl", out var xu) && xu.GetBoolean(),
        j.TryGetProperty("storageQuotaBytes", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : null,
        j.TryGetProperty("storageUsedBytes", out var su) && su.ValueKind == JsonValueKind.Number ? su.GetInt64() : 0,
        j.TryGetProperty("incompleteUploadCleanupDays", out var iu) ? iu.GetInt32() : 0,
        j.TryGetProperty("auditWebhookUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
        j.TryGetProperty("auditWebhookConfigured", out var cf) && cf.ValueKind == JsonValueKind.True,
        j.TryGetProperty("auditWebhookConsecutiveFailures", out var f) ? f.GetInt32() : 0,
        SimplArchiveApiClient.OptDate(j, "auditWebhookLastSuccessAt"),
        SimplArchiveApiClient.OptDate(j, "auditWebhookLastFailureAt"),
        SimplArchiveApiClient.OptDate(j, "auditWebhookNextAttemptAt"),
        j.TryGetProperty("auditWebhookLastError", out var le) && le.ValueKind == JsonValueKind.String ? le.GetString() : null,
        ApiCore.ParseLinks(j));

    private async Task SetRightsCoreAsync(string path, SystemRightsData rights, CancellationToken cancellationToken)
    {
        using var response = await _core.Http.PutAsJsonAsync(path, rights, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You can only grant rights you hold yourself; changing tenant-admin needs a tenant admin.");
        }

        response.EnsureSuccessStatusCode();
    }

    // The normalized PNG bytes, or null if the user has no photo.
    public Task<byte[]?> GetUserPhotoAsync(PrincipalInfo user, CancellationToken cancellationToken = default) =>
        _core.GetPhotoAsync(RequireHref(user, "photo"), cancellationToken);

    public Task DeleteUserPhotoAsync(PrincipalInfo user, CancellationToken cancellationToken = default) =>
        _core.DeletePhotoAsync(Task.FromResult(RequireHref(user, "photo")), cancellationToken);

    private static PrincipalInfo ParseUser(JsonElement e) => new(
        false,
        e.GetProperty("id").GetGuid(),
        e.GetProperty("displayName").GetString() ?? "",
        !e.TryGetProperty("isActive", out var a) || a.ValueKind == JsonValueKind.True,
        ParseRights(e),
        e.TryGetProperty("mfaEnabled", out var mfa) && mfa.ValueKind == JsonValueKind.True,
        e.TryGetProperty("imapShowAllDocuments", out var im) && im.ValueKind == JsonValueKind.True,
        ApiCore.ParseLinks(e));

    private static PrincipalInfo ParseGroup(JsonElement e) => new(
        true,
        e.GetProperty("id").GetGuid(),
        e.GetProperty("name").GetString() ?? "",
        true,
        ParseRights(e),
        false,
        false,
        ApiCore.ParseLinks(e));

    private static SystemRightsData ParseRights(JsonElement e)
    {
        if (!e.TryGetProperty("rights", out var r))
        {
            return new SystemRightsData(false, false, false, false, false, false, false, false, false, false, false, false, false);
        }

        bool B(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new SystemRightsData(
            B("isTenantAdmin"), B("canImpersonate"), B("canOverrideCheckout"), B("canLegalHold"),
            B("canManageClassification"), B("canResetMfa"), B("canManageRepositories"), B("canManageMasks"),
            B("canManageServiceAccounts"), B("canManageUsers"), B("canViewAuditLog"), B("canExport"), B("canImport"),
            B("canManageIntrays"), B("canCreateExternalLink"), B("canAccessWithoutGrant"), B("canManageMailRouting"),
            r.TryGetProperty("clearanceRank", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt32() : 0);
    }


    // The create/update body — the five grantable rights, camelCase over the wire (name + booleans).
    private static object ToServiceAccountBody(string name, SystemRightsData rights) => new
    {
        name,
        canManageRepositories = rights.CanManageRepositories,
        canManageMasks = rights.CanManageMasks,
        canManageServiceAccounts = rights.CanManageServiceAccounts,
        canImport = rights.CanImport,
        canExport = rights.CanExport,
    };



    // The tenant-settings resource's own maintenance actions (issue #416). Both are rels ON that resource, so
    // reaching them means reading it first — paid once per admin click, which is the trade the root's
    // "collection roots only" rule asks for: an action on a resource is advertised by that resource, not by the
    // root. (Contrast the notification badge, which is polled and therefore earned a root rel of its own.)
    private async Task<string> TenantSettingsRelAsync(string rel, CancellationToken cancellationToken)
    {
        var settings = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("tenantSettings", cancellationToken), cancellationToken);
        return ApiCore.ParseLinks(settings) is { } links && links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException($"Tenant settings advertised no '{rel}' rel (ADR 0543).");
    }




    // Sends a synthetic test event to the tenant's saved SIEM webhook (ADR "Audit webhook test delivery") — returns
    // whether the endpoint accepted it + the error on failure.
    public async Task<(bool Success, string? Error)> TestAuditWebhookAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.PostAsync(await TenantSettingsRelAsync("audit-webhook-test", cancellationToken), null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiActionException("Save the webhook URL + secret before sending a test.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException("You don't have permission to test the audit webhook.");
        }

        response.EnsureSuccessStatusCode();
        var j = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (j.GetProperty("success").GetBoolean(),
            j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
    }

    // Links carries the repository's advertised addresses (`document`, `children`) — see #443.
    public sealed record AdminPersonalRepoInfo(Guid UserId, string DisplayName, string Email, bool UserIsActive, Guid RepositoryId, bool HasChildren, bool HasSubfolders,
        IReadOnlyDictionary<string, string>? Links = null)
    {
        public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
    }

    /// <summary>Follows a row's advertised <c>take-over</c> address (ADR 0672).</summary>
    /// <remarks>
    /// Takes the HREF, not a user id: the caller holds the row that advertised it, so there is nothing to
    /// compose and nothing to look up again.
    /// </remarks>
    public async Task TakeOverPersonalSpaceAsync(string takeOverHref, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PostAsync(takeOverHref, JsonContent.Create(new { }), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Lists every user's personal repository (ADR "Tenant-admin Administration → Users view") — tenant-admin only.
    public async Task<List<AdminPersonalRepoInfo>> GetAdminPersonalRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        // The root's `admin` rel leads to the administration index, which advertises this list — two hops, but
        // both of them followed rather than assembled, and paid once per admin screen (ADR 0543).
        var admin = await _core.Http.GetFromJsonAsync<JsonElement>(await _core.RootHrefAsync("admin", cancellationToken), cancellationToken);
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(ApiCore.RequireRel(admin, "personal-repositories", "The administration index"), cancellationToken);
        var list = new List<AdminPersonalRepoInfo>();
        if (json.TryGetProperty("repositories", out var array))
        {
            foreach (var r in array.EnumerateArray())
            {
                list.Add(new AdminPersonalRepoInfo(
                    r.GetProperty("userId").GetGuid(),
                    r.GetProperty("displayName").GetString() ?? "",
                    r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    r.TryGetProperty("userIsActive", out var a) && a.GetBoolean(),
                    r.GetProperty("repositoryId").GetGuid(),
                    r.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
                    r.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
                    ApiCore.ParseLinks(r)));
            }
        }
        return list;
    }
}
