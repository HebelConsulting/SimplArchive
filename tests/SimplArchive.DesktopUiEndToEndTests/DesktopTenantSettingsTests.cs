using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Tenant-admin self-service settings (ADR "Tenant-admin settings tab") via the real desktop api client: the
// demo admin (a tenant admin) reads the settings, updates the default OCR languages + audit retention, reads
// them back, and creates a new repository — all through the real SimplArchiveApiClient against the running Api.
[Collection(UiCollection.Name)]
public class DesktopTenantSettingsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTenantSettingsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Read_update_read_back_and_create_repository()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var before = await api.Admin.GetTenantSettingsAsync();
        Assert.False(string.IsNullOrEmpty(before.Name));

        // Update GROUP BY GROUP via the settings-<group> sub-resources (#530 tranche 10) — keep the same name
        // to avoid a cross-test collision. WORM mode 1 = Compliance. Each save touches only its group; the
        // final response reflects everything, which the asserts below verify.
        await api.Admin.SaveTenantSettingsGroupAsync(before, "capture", new { defaultOcrLanguages = "deu+eng", restrictTagsToCatalog = false });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "records", new { auditRetentionDays = 500, wormLockMode = 1, requireDispositionReview = true });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "checkout", new { checkoutTtlDays = 21, checkoutWarningDays = 3 });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "security", new { requireMfa = true, allowPasskeyLogin = true, enforceClearance = false });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "storage", new { storageQuotaBytes = 500L * 1024 * 1024, incompleteUploadCleanupDays = 14 });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "external-links", new { allowExternalLinks = true, externalLinkMaxDays = 30, externalLinkDefaultAccesses = 2, showExternalLinkUrl = true });
        // The IMAP seed default arrives TRUE (the store default, #793) — flip it OFF so the round-trip proves
        // the save writes rather than the default surviving.
        await api.Admin.SaveTenantSettingsGroupAsync(before, "mail", new { imapShowAllDocumentsDefault = false });
        var updated = await api.Admin.SaveTenantSettingsGroupAsync(before, "audit-streaming", new { auditWebhookUrl = "https://siem.example.com/ingest", auditWebhookSecret = "s3cr3t-signing-key" });
        // External links (issue #385): a group save must not disturb the OTHER groups — the split exists so a
        // forgotten field can no longer silently switch an unrelated feature off.
        Assert.True(updated.AllowExternalLinks);
        Assert.Equal(30, updated.ExternalLinkMaxDays);
        Assert.Equal(2, updated.ExternalLinkDefaultAccesses);
        // Same reasoning for the newest field (issue #412): a full-replacement PUT turns "forgot to send it" into
        // "silently switched it off", so every new tenant setting earns a round-trip assertion here.
        Assert.True(updated.ShowExternalLinkUrl);

        // #793 — flipped off above against a true default, so this asserts the write, not the seed.
        Assert.False(updated.ImapShowAllDocumentsDefault);

        Assert.Equal("deu+eng", updated.DefaultOcrLanguages);
        Assert.Equal(500, updated.AuditRetentionDays);
        Assert.Equal(21, updated.CheckoutTtlDays);
        Assert.Equal(3, updated.CheckoutWarningDays);
        Assert.Equal(1, updated.WormLockMode);
        Assert.True(updated.RequireMfa);
        Assert.True(updated.AllowPasskeyLogin);
        Assert.True(updated.RequireDispositionReview);
        Assert.Equal(500L * 1024 * 1024, updated.StorageQuotaBytes);
        Assert.Equal(14, updated.IncompleteUploadCleanupDays);
        Assert.Equal("https://siem.example.com/ingest", updated.AuditWebhookUrl);
        Assert.True(updated.AuditWebhookConfigured);

        var readBack = await api.Admin.GetTenantSettingsAsync();
        Assert.Equal("deu+eng", readBack.DefaultOcrLanguages);
        Assert.Equal(500, readBack.AuditRetentionDays);
        Assert.Equal(21, readBack.CheckoutTtlDays);
        Assert.Equal(1, readBack.WormLockMode);
        Assert.True(readBack.RequireMfa);
        Assert.True(readBack.AllowPasskeyLogin);
        Assert.Equal("https://siem.example.com/ingest", readBack.AuditWebhookUrl);
        Assert.True(readBack.AuditWebhookConfigured);

        // Restore the original values so a re-run / other tests see a clean tenant (require-MFA OFF for the shared demo admin,
        // passkey login back to the tenant's original state, webhook cleared).
        await api.Admin.SaveTenantSettingsGroupAsync(before, "capture", new { defaultOcrLanguages = before.DefaultOcrLanguages, restrictTagsToCatalog = before.RestrictTagsToCatalog });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "records", new { auditRetentionDays = before.AuditRetentionDays, wormLockMode = before.WormLockMode, requireDispositionReview = before.RequireDispositionReview });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "checkout", new { checkoutTtlDays = before.CheckoutTtlDays, checkoutWarningDays = before.CheckoutWarningDays });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "security", new { requireMfa = before.RequireMfa, allowPasskeyLogin = before.AllowPasskeyLogin, enforceClearance = before.EnforceClearance });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "storage", new { storageQuotaBytes = before.StorageQuotaBytes, incompleteUploadCleanupDays = before.IncompleteUploadCleanupDays });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "external-links", new { allowExternalLinks = before.AllowExternalLinks, externalLinkMaxDays = before.ExternalLinkMaxDays, externalLinkDefaultAccesses = before.ExternalLinkDefaultAccesses, showExternalLinkUrl = before.ShowExternalLinkUrl });
        await api.Admin.SaveTenantSettingsGroupAsync(before, "audit-streaming", new { auditWebhookUrl = (string?)null, auditWebhookSecret = (string?)null });
        var restored = await api.Admin.GetTenantSettingsAsync();
        Assert.Equal(before.AllowPasskeyLogin, restored.AllowPasskeyLogin); // round-trips to the original (passkey login defaults ON, ADR "Passwordless passkey login on by default")
        Assert.False(restored.RequireDispositionReview);
        Assert.Null(restored.AuditWebhookUrl);
        Assert.False(restored.AuditWebhookConfigured);

        // A tenant admin can create a new root repository.
        var repoName = $"Desktop repo {Guid.NewGuid():N}";
        await api.Documents.CreateRepositoryAsync(repoName);
        var repos = await api.Documents.GetRepositoriesAsync();
        Assert.Contains(repos, r => r.Name == repoName);
    }
}
