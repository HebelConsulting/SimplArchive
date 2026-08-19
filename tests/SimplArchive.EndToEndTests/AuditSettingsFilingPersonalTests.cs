using System.Text;

namespace SimplArchive.EndToEndTests;

// End-to-end for the last of every-mutation audit coverage (ADR "Audit tenant-settings, inbox filing +
// personal-repository creation") over the real API + Postgres + object storage: a tenant-settings change is
// audited with a field-level before→after summary (secret redacted), filing an intray item — as a new document
// and as a new version — is audited, and creating a personal repository is audited.
[Collection(E2ECollection.Name)]
public class AuditSettingsFilingPersonalTests
{
    private readonly E2EApiFactory _factory;

    public AuditSettingsFilingPersonalTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_settings_update_is_audited_with_field_level_changes()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);

        // A tenant admin who can also read the audit log (CanViewAuditLog isn't implied by IsTenantAdmin).
        var email = $"settings-admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "settings1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Settings Admin", canViewAuditLog: true);
        await _factory.GrantTenantAdminAsync(email);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Change retention (records group) and require-MFA (security group) — two group PUTs, two events
        // (#530 tranche 10): the trail reads as intent, one scoped action per group.
        await TestJson.Put(admin, "/api/tenant-settings/records", new { auditRetentionDays = 90, wormLockMode = 0, requireDispositionReview = false });
        await TestJson.Put(admin, "/api/tenant-settings/security", new { requireMfa = true, allowPasskeyLogin = false, enforceClearance = false });

        // Each group's audit event carries a field-level before→after summary scoped to ITS fields.
        var recordsEvents = (await TestJson.Get(admin, "/api/audit-events?action=Tenant.SettingsRecordsUpdated")).GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(recordsEvents);
        var details = recordsEvents[0].GetProperty("details").GetString()!;
        Assert.Contains("Audit retention days 365→90", details);
        Assert.DoesNotContain("Require MFA", details); // the security change is NOT in the records event

        var securityEvents = (await TestJson.Get(admin, "/api/audit-events?action=Tenant.SettingsSecurityUpdated")).GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(securityEvents);
        details = securityEvents[0].GetProperty("details").GetString()!;
        Assert.Contains("Require MFA off→on", details);
        // The webhook secret's value is never in the log.
        Assert.DoesNotContain("secret", details, StringComparison.OrdinalIgnoreCase); // no webhook change here
    }

    [Fact]
    public async Task Intray_filing_and_personal_repository_creation_are_audited()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"filer-{Guid.NewGuid():N}@e2e.local";
        const string password = "filer1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Filer", canViewAuditLog: true);
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Get-or-create the personal repository (audited as Repository.Created).
        var personal = await TestJson.Post(user, "/api/me/personal-repository", new { });
        var repoId = personal.GetProperty("id").GetGuid();

        // Upload an intray item and file it into My Documents (a new document). Not the personal ROOT: that
        // level holds only the folders it was provisioned with (#634), so filing there is refused.
        var myDocumentsId = (await TestJson.Get(user, $"/api/documents/{repoId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents")
            .GetProperty("id").GetGuid();
        var docId = await UploadAndFileAsync(user, "note.txt", new { folderId = myDocumentsId });

        // Upload another and file it as a new version of that document.
        await UploadAndFileAsync(user, "note2.txt", new { documentId = docId });

        // The audit log carries the personal-repo creation and both filings.
        var events = (await TestJson.Get(user, "/api/audit-events?limit=200")).GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "Repository.Created"
            && e.GetProperty("details").GetString() == "Personal repository created");
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "Document.Filed"
            && e.GetProperty("details").GetString() == "Filed from intray as a new document");
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "Document.Filed"
            && e.GetProperty("details").GetString() == "Filed from intray as a new version");
    }

    private static async Task<Guid> UploadAndFileAsync(HttpClient user, string name, object fileBody)
    {
        var upload = await TestJson.Post(user, "/api/intray", new { fileName = name });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("hello")))).EnsureSuccessStatusCode();
        }

        var filed = await TestJson.Post(user, $"/api/intray/{name}/file", fileBody);
        return filed.GetProperty("id").GetGuid();
    }
}
