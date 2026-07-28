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

        var before = await api.GetTenantSettingsAsync();
        Assert.False(string.IsNullOrEmpty(before.Name));

        // Update the editable settings (keep the same name to avoid a cross-test name collision). WORM mode 1 = Compliance.
        // Also configure the audit webhook (URL + write-only secret) and confirm it reports back as configured.
        var updated = await api.SetTenantSettingsAsync(before.Name, "deu+eng", 500, 21, 3, 1, true, true, true, false, false, 500L * 1024 * 1024, 14, "https://siem.example.com/ingest", "s3cr3t-signing-key");
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

        var readBack = await api.GetTenantSettingsAsync();
        Assert.Equal("deu+eng", readBack.DefaultOcrLanguages);
        Assert.Equal(500, readBack.AuditRetentionDays);
        Assert.Equal(21, readBack.CheckoutTtlDays);
        Assert.Equal(1, readBack.WormLockMode);
        Assert.True(readBack.RequireMfa);
        Assert.True(readBack.AllowPasskeyLogin);
        Assert.Equal("https://siem.example.com/ingest", readBack.AuditWebhookUrl);
        Assert.True(readBack.AuditWebhookConfigured);

        // Restore the original values so a re-run / other tests see a clean tenant (require-MFA OFF for the shared demo admin,
        // passkey login OFF, webhook cleared).
        await api.SetTenantSettingsAsync(before.Name, before.DefaultOcrLanguages, before.AuditRetentionDays, before.CheckoutTtlDays, before.CheckoutWarningDays, before.WormLockMode, before.RequireMfa, before.AllowPasskeyLogin, before.RequireDispositionReview, before.RestrictTagsToCatalog, before.EnforceClearance, before.StorageQuotaBytes, before.IncompleteUploadCleanupDays, null, null);
        var restored = await api.GetTenantSettingsAsync();
        Assert.False(restored.AllowPasskeyLogin);
        Assert.False(restored.RequireDispositionReview);
        Assert.Null(restored.AuditWebhookUrl);
        Assert.False(restored.AuditWebhookConfigured);

        // A tenant admin can create a new root repository.
        var repoName = $"Desktop repo {Guid.NewGuid():N}";
        await api.CreateRepositoryAsync(repoName);
        var repos = await api.GetRepositoriesAsync();
        Assert.Contains(repos, r => r.Name == repoName);
    }
}
