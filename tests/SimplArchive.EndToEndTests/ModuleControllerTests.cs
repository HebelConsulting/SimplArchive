using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// The module-controller slice's activation circle (ADR 0737), over the real wire and the REAL loader (the
// TestModule dll is staged into a Modules/ directory by the factory): before activation the module's
// surface does not exist — no root rel, 404 MODULE_NOT_ACTIVE on its routes; filing a vendor-signed
// license activates it — the rel appears, the controller answers through the ABI's caller/rights seams,
// its refusals wear the core's RFC 7807 shape; and a license lapsed past grace switches it all off again.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ModuleControllerTests
{
    private readonly E2EApiFactory _factory;

    public ModuleControllerTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId);

    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Mods {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"modadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "modadmin-1234";
        // canManageServiceAccounts on top of tenant-admin: the consent act needs to FIND the module's
        // principal in the service-accounts listing, whose gate is that specific system right.
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Module Admin", isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        return new Rig(admin, owner, tenantId, repoId);
    }

    private static async Task<bool> RootAdvertisesAsync(HttpClient client) =>
        (await TestJson.Get(client, "/api")).GetProperty("links").EnumerateArray()
            .Any(l => l.GetProperty("rel").GetString() == "test-module:status");

    private static async Task<Guid> FileLicenseAsync(Rig rig, DateOnly supportEnd, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], rig.TenantId, supportEnd, ModuleAbiVersion.Major, string.Empty)
            .Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var docId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(rig.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(rig.Owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        return docId;
    }

    private async Task ActivateAsync(Rig rig, ECDsa vendorKey, bool grantPrincipal = true)
    {
        var licenseDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), vendorKey);
        await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = licenseDocId });
        if (grantPrincipal)
        {
            await GrantPrincipalAsync(rig);
        }
    }

    /// <summary>The consent act (ADR 0736): an ordinary ACL grant to the module's own login-less
    /// principal — created by the activation, listed like any service account.</summary>
    private static async Task GrantPrincipalAsync(Rig rig)
    {
        var principalId = (await TestJson.Get(rig.Admin, "/api/service-accounts"))
            .GetProperty("serviceAccounts").EnumerateArray()
            .Single(sa => sa.GetProperty("name").GetString() == "Module: Test Module")
            .GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{rig.RepoId}/acl-entries/service-accounts/{principalId}",
            new { canSee = true });
    }

    [Fact]
    public async Task A_transition_is_a_labeled_action_with_a_diagnosis_when_red_and_a_commit_when_green()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey, grantPrincipal: false);

            // A subject document — a dossier wearing the machine's subject mask (seeded at activation).
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();

            // The generic action surface (ADR 0743): the transition arrives as a LABELED POST link — the
            // exact shape both clients' shipped parser turns into a button, rel unknown to either.
            var document = await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}");
            var action = document.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "machine:test-pilot:log-entry");
            Assert.Equal("Log entry", action.GetProperty("label").GetString());
            Assert.Equal("POST", action.GetProperty("method").GetString());

            // CONSENT FIRST (ADR 0736): the module is active but UNGRANTED — its principal holds nothing,
            // so the machine's own reads see no evidence and the gate honestly refuses even before any
            // certificate question arises. The grant below is what changes the answer.
            await GrantPrincipalAsync(rig);

            // RED: no certificate filed — the refusal IS the diagnosis (ADR 0742), sentence in detail,
            // machine-readable codes in the problem's extensions.
            var refused = await rig.Admin.PostAsync(action.GetProperty("href").GetString(), null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
            Assert.Equal("MACHINE_TRANSITION_REFUSED", problem.GetProperty("errorCode").GetString());
            Assert.Contains(problem.GetProperty("refusals").EnumerateArray(),
                r => r.GetProperty("code").GetString() == "test.certificate-expired");

            // File a valid certificate, and the same click commits: the handler's write (a new child
            // through the facade) lands inside the engine's transaction.
            // An ITEM-masked child: created plain, then restamped — the children create-with-mask path
            // makes folders only, and a certificate is a document.
            var certificateId = (await TestJson.Post(rig.Owner, $"/api/documents/{dossierId}/children",
                new { name = "Medical" })).GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/mask",
                new { maskId = SimplArchive.TestModule.TestModule.CertificateMaskId });
            var validTo = (await TestJson.Get(rig.Admin, $"/api/masks/{SimplArchive.TestModule.TestModule.CertificateMaskId}"))
                .GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "Valid to").GetProperty("id").GetGuid();
            await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/index-data",
                new { fields = new[] { new { fieldDefinitionId = validTo, values = new[] { DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)).ToString("yyyy-MM-dd") } } } });

            var beforeGreen = await ChildCountAsync(rig.Admin, dossierId);
            Assert.Equal(HttpStatusCode.NoContent, (await rig.Admin.PostAsync(action.GetProperty("href").GetString(), null)).StatusCode);
            var afterGreen = await ChildCountAsync(rig.Admin, dossierId);
            Assert.Equal(beforeGreen + 1, afterGreen); // exactly the handler's one entry

            // ROLLBACK over the wire: the fixture's exploding handler writes and then throws — the caller
            // sees the failure, and what the handler wrote is never visible (ADR 0737's transaction).
            var exploded = await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/explode", null);
            Assert.Equal(HttpStatusCode.InternalServerError, exploded.StatusCode);
            Assert.Equal(afterGreen, await ChildCountAsync(rig.Admin, dossierId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task A_documents_resource_carries_its_machines_derived_statuses_with_diagnoses()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();

            // BEFORE any certificate: the test-pilot machine's MayAct status rides the document (#1021),
            // unmet, carrying the ADR-0742 diagnoses a client renders — the whole point of surfacing it.
            var doc = await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}");
            var mayAct = doc.GetProperty("machineStatuses").EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "MayAct");
            Assert.False(mayAct.GetProperty("satisfied").GetBoolean());
            Assert.Contains(mayAct.GetProperty("failures").EnumerateArray(),
                f => f.GetProperty("code").GetString() == "test.certificate-expired");

            // A document no machine watches carries an empty set, not a missing field.
            var plain = await TestJson.Get(rig.Admin, $"/api/documents/{rig.RepoId}");
            Assert.Empty(plain.GetProperty("machineStatuses").EnumerateArray());

            // The localization seam, end to end (ABI 0.10, ADR 0767): the SAME diagnosis in German when the
            // request says so — the engine resolved the module's catalog for the request culture — and the
            // untranslated sibling code falls back to its composed English rather than to silence.
            using var germanRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{dossierId}");
            germanRequest.Headers.AcceptLanguage.ParseAdd("de");
            var german = await TestJson.Read(await rig.Admin.SendAsync(germanRequest));
            var germanMayAct = german.GetProperty("machineStatuses").EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "MayAct");
            Assert.Contains(germanMayAct.GetProperty("failures").EnumerateArray(),
                f => f.GetProperty("code").GetString() == "test.certificate-expired"
                    && f.GetProperty("text").GetString()!.Contains("abgelaufen"));
            Assert.Contains(germanMayAct.GetProperty("failures").EnumerateArray(),
                f => f.GetProperty("code").GetString() == "test.recency"
                    && f.GetProperty("text").GetString()!.Contains("recent landings")); // no catalog entry → English

            // And a REFUSED transition's problem carries the module marker + the localized sentence — the
            // clients' license to render the detail.
            using var refusedRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/documents/{dossierId}/machine/test-pilot/transitions/log-entry");
            refusedRequest.Headers.AcceptLanguage.ParseAdd("de");
            var refusedResponse = await rig.Admin.SendAsync(refusedRequest);
            Assert.Equal(HttpStatusCode.Conflict, refusedResponse.StatusCode);
            var problem = await refusedResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("MACHINE_TRANSITION_REFUSED", problem.GetProperty("errorCode").GetString());
            Assert.Equal("test-module", problem.GetProperty("module").GetString());
            Assert.Contains("abgelaufen", problem.GetProperty("detail").GetString());

            // The OTHER refusal path (found live 2026-09-08): an exception thrown INSIDE a handler reaches
            // ApiExceptionHandler after the localization middleware's scope has unwound, so the ambient
            // culture is the default — the handler must resolve the REQUEST's culture feature instead.
            // Before the fix this returned the English template while stamping the module marker beside it.
            using var handlerRefusal = new HttpRequestMessage(HttpMethod.Post,
                $"/api/documents/{dossierId}/machine/test-pilot/transitions/refuse");
            handlerRefusal.Headers.AcceptLanguage.ParseAdd("de");
            var handlerResponse = await rig.Admin.SendAsync(handlerRefusal);
            Assert.Equal(HttpStatusCode.Conflict, handlerResponse.StatusCode);
            var handlerProblem = await handlerResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("TEST_HANDLER_REFUSED", handlerProblem.GetProperty("errorCode").GetString());
            Assert.Equal("test-module", handlerProblem.GetProperty("module").GetString());
            Assert.Equal("Der Testschritt wurde abgelehnt.", handlerProblem.GetProperty("detail").GetString());

            // The proposal surface (ABI 0.11, ADR 0769), end to end: the document advertises the labeled
            // GET rel; following it answers with the field it fills and the already-filtered items — every
            // OTHER dossier, never the asker (the fixture's one rule). The picker's whole contract.
            var mentorId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Mentor {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();
            var withProposal = await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}");
            var proposalLink = withProposal.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "machine-proposal:test-pilot:mentors");
            Assert.Equal("Propose mentor", proposalLink.GetProperty("label").GetString());

            var proposal = await TestJson.Get(rig.Admin, proposalLink.GetProperty("href").GetString()!);
            Assert.Equal("Mentor", proposal.GetProperty("fillsField").GetString());
            Assert.Equal("Propose mentor", proposal.GetProperty("label").GetString());
            var mentorNames = proposal.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("value").GetString()).ToList();
            Assert.Contains(mentorNames, n => n!.StartsWith("Mentor "));
            Assert.DoesNotContain(mentorNames, n => n!.StartsWith("Dossier ")); // the asker proposes others, never itself
            Assert.All(proposal.GetProperty("items").EnumerateArray(),
                i => Assert.Equal("a test mentor", i.GetProperty("detail").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task The_fact_gated_act_reads_the_modules_projection_and_rebuild_rederives_it()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();
            await FileValidCertificateAsync(rig, dossierId);

            // The fact-gated act (ADRs 0736/0738 over the wire): refused while the module's OWN counter —
            // a real table the host migrated, not a computed aggregate — reads zero, with the value named.
            var refused = await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("0 recent landings",
                JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync())
                    .GetProperty("detail").GetString());

            // Three logged entries — each handler incrementing the projection in ITS act's transaction —
            // and the gate opens.
            for (var i = 0; i < 3; i++)
            {
                var logged = await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/log-entry", null);
                Assert.Equal(HttpStatusCode.NoContent, logged.StatusCode);
                // A DELIBERATE transition carries no populate-outcome header (ADR 0814): the cooldown and
                // its signal are the auto-refresh hook's alone — a real act must never be skipped as
                // "already current", and running the same act three times here is exactly the proof.
                Assert.False(logged.Headers.Contains(
                    SimplArchive.Api.Controllers.MachineTransitionsController.PopulateOutcomeHeader));
            }

            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null)).StatusCode);

            // Wipe the projection UNDER the module — the support-case scenario — and the gate honestly
            // closes...
            await using (var db = new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("ConnectionStrings__Default")))
            {
                await db.OpenAsync();
                await using var wipe = new Npgsql.NpgsqlCommand("DELETE FROM tm_landing_counters", db);
                await wipe.ExecuteNonQueryAsync();
            }

            Assert.Equal(HttpStatusCode.Conflict,
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null)).StatusCode);

            // ...until the REBUILD re-derives it from the documents (ADR 0738's operator guarantee): the
            // admin endpoint, then the same act passes again.
            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsync("/api/modules/test-module/rebuild/landings", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/certify", null)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task An_auto_refresh_transition_stages_content_on_open_and_replaces_it_in_place()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey); // grants the principal too, so its GetChildren sees the folder

            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                .GetProperty("id").GetGuid();

            // The populate-on-open hook arrives as a DISTINCT rel (ABI 0.6) the clients auto-POST on open —
            // not the plain machine: action rel, so a client can tell "run this automatically" from "a button".
            var document = await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}");
            var refresh = document.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "machine-auto-refresh:test-pilot:refresh");
            Assert.Equal("POST", refresh.GetProperty("method").GetString());
            Assert.Equal("Refresh", refresh.GetProperty("label").GetString());
            Assert.Equal(0, await ChildCountAsync(rig.Admin, dossierId));

            // Opening the folder (a client following the rel): the handler STAGES a content-bearing document
            // under it — content-bearing creation over the real wire and real object storage, which nothing
            // in the ABI could do before 0.6. The outcome header says the hook RAN (ADR 0814) — the signal a
            // client reloads the folder on.
            var first = await rig.Admin.PostAsync(refresh.GetProperty("href").GetString(), null);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal("ran", first.Headers.GetValues(
                SimplArchive.Api.Controllers.MachineTransitionsController.PopulateOutcomeHeader).Single());
            var children = (await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}/children")).GetProperty("children");
            Assert.Equal(1, children.GetArrayLength());
            Assert.Equal("Staged entry", children.EnumerateArray().Single().GetProperty("name").GetString());

            // Opening again inside the staged content's own expiry is a NO-OP (ADR 0814, #1309): the hook
            // owns its cooldown on this path exactly as on the protocol one, so the second POST answers
            // "current" — success, nothing ran, nothing to reload — rather than issuing a second upstream
            // fetch. (The handler's replace-in-place behaviour still runs once the content expires;
            // ProtocolReadRefreshRunnerTests drives that with an expired row.)
            var second = await rig.Admin.PostAsync(refresh.GetProperty("href").GetString(), null);
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
            Assert.Equal("current", second.Headers.GetValues(
                SimplArchive.Api.Controllers.MachineTransitionsController.PopulateOutcomeHeader).Single());
            Assert.Equal(1, await ChildCountAsync(rig.Admin, dossierId));

            // The hook FOLLOWS the content (ADR 0764): the staged CHILD carries its parent's rel too, same
            // href — so a client selecting the leaf refreshes without a second fetch to find the affordance.
            var childId = (await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}/children"))
                .GetProperty("children").EnumerateArray().Single().GetProperty("id").GetGuid();
            var childRefresh = (await TestJson.Get(rig.Admin, $"/api/documents/{childId}")).GetProperty("links")
                .EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "machine-auto-refresh:test-pilot:refresh");
            Assert.Equal(refresh.GetProperty("href").GetString(), childRefresh.GetProperty("href").GetString());

            // And CanSee is the whole ask (ADR 0764 — the update is AUTOMATED; the module principal writes):
            // a read-only viewer sees the rel and may POST it, while an ordinary transition stays edit-gated.
            // The grant goes on the REPOSITORY — the dossier's governing ACL scope. An entry on the dossier
            // itself would be inert (#1060): the dossier does not break inheritance, so the walk (ADR 0183)
            // resolves rights from the nearest breaking ancestor-or-self, which is the root.
            var viewerEmail = $"viewer-{Guid.NewGuid():N}@e2e.local";
            var viewerId = await _factory.SeedUserAsync(rig.TenantId, viewerEmail, "ViewerPw123!", "Viewer");
            await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{viewerId}",
                new { canSee = true, canReadContent = true });
            using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, "ViewerPw123!"));
            var viewerDoc = await TestJson.Get(viewer, $"/api/documents/{dossierId}");
            var viewerRefresh = viewerDoc.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "machine-auto-refresh:test-pilot:refresh");
            Assert.Equal(HttpStatusCode.NoContent,
                (await viewer.PostAsync(viewerRefresh.GetProperty("href").GetString(), null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await viewer.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/log-entry", null)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    private async Task FileValidCertificateAsync(Rig rig, Guid dossierId)
    {
        var certificateId = (await TestJson.Post(rig.Owner, $"/api/documents/{dossierId}/children",
            new { name = "Medical" })).GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/mask",
            new { maskId = SimplArchive.TestModule.TestModule.CertificateMaskId });
        var validTo = (await TestJson.Get(rig.Admin, $"/api/masks/{SimplArchive.TestModule.TestModule.CertificateMaskId}"))
            .GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Valid to").GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{certificateId}/index-data",
            new { fields = new[] { new { fieldDefinitionId = validTo, values = new[] { DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)).ToString("yyyy-MM-dd") } } } });
    }

    private static async Task<int> ChildCountAsync(HttpClient client, Guid folderId) =>
        (await TestJson.Get(client, $"/api/documents/{folderId}/children")).GetProperty("children").GetArrayLength();

    [Fact]
    public async Task The_activation_circle_switches_the_modules_surface_on_and_off()
    {
        var rig = await RigAsync();

        // BEFORE: the module's surface does not exist for this tenant — no rel at the root (ADR 0543),
        // and the route answers 404 with the reason named for an administrator reading the wire.
        Assert.False(await RootAdvertisesAsync(rig.Admin));
        var refused = await rig.Admin.GetAsync("/api/test-module/status");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
        Assert.Equal("MODULE_NOT_ACTIVE", problem.GetProperty("errorCode").GetString());

        // ACTIVATE: plant the vendor verify key (environment, because the loaded module lives in its own
        // context — no key material in the repo), file a signed license, and follow the license PUT.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var licenseDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), vendorKey);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = licenseDocId });

            // AFTER: the rel exists, the controller answers through the seams, and a module refusal is an
            // RFC 7807 problem indistinguishable in shape from a core one.
            Assert.True(await RootAdvertisesAsync(rig.Admin));
            var status = await TestJson.Get(rig.Admin, "/api/test-module/status");
            Assert.Equal(rig.TenantId, status.GetProperty("tenantId").GetGuid());
            Assert.True(status.GetProperty("isTenantAdmin").GetBoolean());
            // The identity seam (ABI 0.2, #1014): the caller as a person would name them.
            Assert.Equal("Module Admin", status.GetProperty("callerDisplayName").GetString());
            Assert.EndsWith("@e2e.local", status.GetProperty("callerEmail").GetString());
            Assert.Contains(status.GetProperty("links").EnumerateArray(),
                l => l.GetProperty("rel").GetString() == "self");

            // The rights seam: the caller's effective rights on a real document, answered by the core
            // calculator — visible document → the module sees CanSee...
            var rights = await TestJson.Get(rig.Admin, $"/api/test-module/documents/{licenseDocId}/rights");
            Assert.True(rights.GetProperty("canSee").GetBoolean());

            // THE PATH #1223 WAS FILED ABOUT: the facade's content write, reached from the module's OWN HTTP
            // controller, where nothing has opened a transaction. Every other module test arrives through a
            // transition, which the engine wraps (ADR 0737) — so this path had no caller at all, and the
            // measurement behind #1171 reported zero unwrapped finalizations while this one would have thrown.
            //
            // It asserts 204, not an absence of errors: before the facade moved to FileAsync this answered 500,
            // because DocumentFinalizer REFUSES to run outside a transaction since #1171. A module that filed
            // content from its own endpoint worked yesterday and failed today, with nothing in-repo to notice.
            var replaced = await rig.Admin.PostAsync(
                $"/api/test-module/documents/{licenseDocId}/replace-content", content: null);
            Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);

            // ...unknown document → the module's own intent-named refusal, in the core's problem shape.
            var invisible = await rig.Admin.GetAsync($"/api/test-module/documents/{Guid.NewGuid()}/rights");
            Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);
            Assert.Equal("TEST_DOCUMENT_NOT_VISIBLE",
                JsonSerializer.Deserialize<JsonElement>(await invisible.Content.ReadAsStringAsync())
                    .GetProperty("errorCode").GetString());

            // LAPSE: a genuine renewal whose support contract ended beyond the 30-day grace — the derived
            // active answer flips (ADR 0740) and the whole surface withdraws again.
            var lapsedDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-40)), vendorKey);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = lapsedDocId });

            Assert.False(await RootAdvertisesAsync(rig.Admin));
            Assert.Equal(HttpStatusCode.NotFound, (await rig.Admin.GetAsync("/api/test-module/status")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    // ABI 1.6 (ADR 0897): a principal-invoked act belongs to the person the subject names, the first of
    // Instructor, Pilot holding a value, or a tenant administrator. A READER who is that person is offered it and
    // may run it; an EDITOR who is not is neither offered it nor allowed it. The link and the POST answer alike.
    [Fact]
    public async Task A_principal_invoked_act_is_offered_to_and_allowed_for_the_named_person_only()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        var clients = new List<HttpClient>();
        try
        {
            await ActivateAsync(rig, vendorKey);

            async Task<(HttpClient Client, string Email)> PersonAsync(string role, bool canEdit)
            {
                var email = $"{role}-{Guid.NewGuid():N}@e2e.local";
                var id = await _factory.SeedUserAsync(rig.TenantId, email, "person-1234", role);
                await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{id}",
                    new { canSee = true, canReadContent = true, canEditContent = canEdit });
                var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "person-1234"));
                clients.Add(client);
                return (client, email);
            }

            var (pilot, pilotEmail) = await PersonAsync("pilot", canEdit: false);
            var (instructor, instructorEmail) = await PersonAsync("instructor", canEdit: false);
            var (editor, _) = await PersonAsync("editor", canEdit: true);

            var fields = (await TestJson.Get(rig.Admin, $"/api/masks/{SimplArchive.TestModule.TestModule.DossierMaskId}"))
                .GetProperty("fields").EnumerateArray()
                .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("id").GetGuid());

            async Task<Guid> DossierAsync(string? instructorValue, string pilotValue)
            {
                var id = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                    new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId }))
                    .GetProperty("id").GetGuid();
                var values = new List<object> { new { fieldDefinitionId = fields["Pilot"], values = new[] { pilotValue } } };
                if (instructorValue is not null)
                {
                    values.Add(new { fieldDefinitionId = fields["Instructor"], values = new[] { instructorValue } });
                }

                await TestJson.Put(rig.Admin, $"/api/documents/{id}/index-data", new { fields = values });
                return id;
            }

            static async Task<bool> OffersAsync(HttpClient client, Guid documentId) =>
                (await TestJson.Get(client, $"/api/documents/{documentId}")).GetProperty("links").EnumerateArray()
                    .Any(l => l.GetProperty("rel").GetString() == "machine:test-pilot:countersign");

            static async Task<HttpStatusCode> InvokeAsync(HttpClient client, Guid documentId) =>
                (await client.PostAsync($"/api/documents/{documentId}/machine/test-pilot/transitions/countersign", null)).StatusCode;

            // SOLO: no instructor, so the pilot is the person, matched case-insensitively as an e-mail is.
            var solo = await DossierAsync(instructorValue: null, pilotValue: pilotEmail.ToUpperInvariant());
            Assert.True(await OffersAsync(pilot, solo));
            Assert.Equal(HttpStatusCode.NoContent, await InvokeAsync(pilot, solo));
            Assert.False(await OffersAsync(editor, solo));     // edit rights alone no longer suffice
            Assert.Equal(HttpStatusCode.Forbidden, await InvokeAsync(editor, solo));
            Assert.True(await OffersAsync(rig.Admin, solo));   // the tenant administrator always may

            // DUAL: the instructor is named, so the instructor (not the pilot) is the person.
            var dual = await DossierAsync(instructorEmail, pilotEmail);
            Assert.False(await OffersAsync(pilot, dual));
            Assert.Equal(HttpStatusCode.Forbidden, await InvokeAsync(pilot, dual));
            Assert.True(await OffersAsync(instructor, dual));
            Assert.Equal(HttpStatusCode.NoContent, await InvokeAsync(instructor, dual));

            // An ORDINARY act on the same subject is untouched: still the editor's, never the reader's.
            Assert.DoesNotContain((await TestJson.Get(pilot, $"/api/documents/{solo}")).GetProperty("links").EnumerateArray(),
                l => l.GetProperty("rel").GetString() == "machine:test-pilot:log-entry");
            Assert.Contains((await TestJson.Get(editor, $"/api/documents/{solo}")).GetProperty("links").EnumerateArray(),
                l => l.GetProperty("rel").GetString() == "machine:test-pilot:log-entry");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            clients.ForEach(c => c.Dispose());
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    // ABI 1.7 (ADR 0898): a module's claimed ROOT route answers protocol credentials, never the core's login. The
    // credential names its tenant; the core sets it, runs the gate, acts as the module's principal, and only then
    // asks the module, whose facade already answers. A refusal is a 401 with the module's challenge.
    [Fact]
    public async Task A_claimed_root_route_authenticates_the_modules_own_credentials_exclusively()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        using var wire = _factory.CreateClient();
        var credential = ModuleCredentialFormat.Compose(rig.TenantId, SimplArchive.TestModule.TestCredentialAuthenticator.Secret);
        try
        {
            async Task<HttpResponseMessage> FeedAsync(string? scheme = null, string? value = null, string? header = null, string path = "/nuget/test-feed")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                if (scheme is not null)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, value);
                }

                if (header is not null)
                {
                    request.Headers.Add(header, value);
                }

                return await wire.SendAsync(request);
            }

            // Before activation the module does not exist for this tenant, credential or not.
            var inactive = await FeedAsync("Bearer", credential);
            Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);
            Assert.Contains("MODULE_NOT_ACTIVE", await inactive.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await ActivateAsync(rig, vendorKey);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/settings",
                new { values = new Dictionary<string, string?> { ["endpoint"] = "https://feed.example" } });

            // The protocol's first attempt is anonymous; the challenge is what makes the client send credentials.
            var anonymous = await FeedAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("Basic realm=\"SimplArchive test feed\"", anonymous.Headers.WwwAuthenticate.ToString());

            // Basic (the user name is ignored), Bearer and the module's API-key header all carry the same credential.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"whoever:{credential}"));
            foreach (var response in new[] { await FeedAsync("Basic", basic), await FeedAsync("Bearer", credential), await FeedAsync(header: "X-Test-ApiKey", value: credential) })
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                // The setting in the subject was read through the FACADE inside AuthenticateAsync: proof that the
                // tenant and the module's principal were in place before the module was asked.
                Assert.Equal("customer-1@https://feed.example", body.GetProperty("subject").GetString());
                Assert.Equal(rig.TenantId, body.GetProperty("tenantId").GetGuid());
                Assert.Equal(JsonValueKind.Null, body.GetProperty("userId").ValueKind);
                Assert.NotEqual(JsonValueKind.Null, body.GetProperty("serviceAccountId").ValueKind);
            }

            // Refusals: a wrong secret, a credential not in the core's format, another tenant's id.
            Assert.Equal(HttpStatusCode.Unauthorized, (await FeedAsync("Bearer", ModuleCredentialFormat.Compose(rig.TenantId, "wrong"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await FeedAsync("Bearer", "open-sesame")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await FeedAsync("Bearer", ModuleCredentialFormat.Compose(Guid.NewGuid(), "open-sesame"))).StatusCode);

            // EXCLUSIVE: the core's own login means nothing here, even an administrator's.
            Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Admin.GetAsync("/nuget/test-feed")).StatusCode);

            // An unknown path under a claimed prefix is the module's 404, never the web client's fallback page.
            var unknown = await FeedAsync("Bearer", credential, path: "/nuget/no-such-thing");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.DoesNotContain("<html", await unknown.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    // ABI 1.8 (ADR 0899): the facade presigns a document's content for a module that redirects rather than
    // proxies (the bytes, a clamped lifetime, the consent gate), and an action may reveal a value once, which the
    // host marks no-store.
    [Fact]
    public async Task A_module_presigns_content_it_may_see_and_reveals_a_value_once_uncached()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);   // grants the module principal canSee on the repository
            var bytes = Encoding.UTF8.GetBytes($"presign me {Guid.NewGuid():N}");
            var granted = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Package {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            var version = await TestJson.Post(rig.Owner, $"/api/documents/{granted}/versions", new { fileExtension = ".txt" });
            using (var storage = new HttpClient())
            {
                (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
            }

            await TestJson.Put(rig.Owner, $"/api/documents/{granted}/versions/{version.GetProperty("id").GetGuid()}", new { });

            var issued = await TestJson.Get(rig.Admin, $"/api/test-module/documents/{granted}/content-url?ttlSeconds=3600");
            var url = new Uri(issued.GetProperty("url").GetString()!);
            using (var anonymous = new HttpClient())
            {
                Assert.Equal(bytes, await anonymous.GetByteArrayAsync(url));   // a plain client follows it to the bytes
            }

            var expires = System.Web.HttpUtility.ParseQueryString(url.Query)["X-Amz-Expires"];
            Assert.InRange(int.Parse(expires!, System.Globalization.CultureInfo.InvariantCulture), 1, 300);   // an hour asked, five minutes given

            // A document the module's principal was never granted: no URL, exactly as a content read sees nothing.
            var elsewhere = (await TestJson.Post(rig.Owner, "/api/repositories", new { name = $"Ungranted {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            Assert.Equal(JsonValueKind.Null, (await TestJson.Get(rig.Admin, $"/api/test-module/documents/{elsewhere}/content-url?ttlSeconds=60")).GetProperty("url").ValueKind);

            // Reveal once: the value arrives in the documented shape, and nothing along the way may keep it.
            var revealed = await rig.Admin.PostAsync("/api/test-module/reveal", null);
            Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
            Assert.True(revealed.Headers.CacheControl?.NoStore);
            var body = JsonSerializer.Deserialize<JsonElement>(await revealed.Content.ReadAsStringAsync());
            Assert.Equal("Issued.", body.GetProperty("message").GetString());
            Assert.Equal("s3cret-shown-once", body.GetProperty("revealOnce").GetProperty("value").GetString());
            Assert.Equal("Test secret", body.GetProperty("revealOnce").GetProperty("label").GetString());

            // An ordinary module response is not marked: no-store follows the revealed value, not the module.
            Assert.NotEqual(true, (await rig.Admin.GetAsync("/api/test-module/status")).Headers.CacheControl?.NoStore);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    // ABI 1.9 (ADR 0900): every write a module makes through the facade is audited as the module's act (field
    // NAMES, never values), a named event lands as {moduleId}.{action}, and the populate hook's ephemeral staging is
    // left out as transitions leave it out.
    [Fact]
    public async Task A_modules_facade_writes_and_named_events_reach_the_audit_trail()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            var dossierName = $"Dossier {Guid.NewGuid():N}";
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = dossierName, maskId = SimplArchive.TestModule.TestModule.DossierMaskId })).GetProperty("id").GetGuid();

            Assert.Equal(HttpStatusCode.NoContent,
                (await rig.Admin.PostAsJsonAsync($"/api/test-module/documents/{dossierId}/mentor", new { mentor = "anna@e2e.local" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,   // the populate hook stages ephemeral content: NOT audited
                (await rig.Admin.PostAsync($"/api/documents/{dossierId}/machine/test-pilot/transitions/refresh", null)).StatusCode);

            var viewerEmail = $"auditor-{Guid.NewGuid():N}@e2e.local";
            await _factory.SeedUserAsync(rig.TenantId, viewerEmail, "audit-1234", "Auditor", canViewAuditLog: true);
            using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, "audit-1234"));
            var events = (await TestJson.Get(viewer, "/api/audit-events?limit=200")).GetProperty("events").EnumerateArray().ToList();
            string? Details(JsonElement e) => e.TryGetProperty("details", out var d) ? d.GetString() : null;

            // The floor: the write, by the module, naming the field and never its value.
            var floor = Assert.Single(events, e => e.GetProperty("action").GetString() == "Document.IndexDataUpdated"
                && Details(e)?.StartsWith("by module test-module", StringComparison.Ordinal) == true);
            Assert.Equal("by module test-module: fields Mentor", Details(floor));
            Assert.DoesNotContain("anna@", Details(floor), StringComparison.Ordinal);

            // The named event, prefixed with the module id, with the module's own sentence.
            var named = Assert.Single(events, e => e.GetProperty("action").GetString() == "test-module.MentorSet");
            Assert.Equal("mentor is now anna@e2e.local", Details(named));

            // The ephemeral staging left no "created by module" line behind.
            Assert.DoesNotContain(events, e => e.GetProperty("action").GetString() == "Document.Created"
                && Details(e)?.StartsWith("by module test-module", StringComparison.Ordinal) == true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    // A MODULE's exclusive folder admits only what it declares, and the children endpoint must agree with what
    // SaveChanges enforces: an unnamed child is an item-to-be (no mask yet), which may then become an admitted item;
    // a named folder it does not admit is refused. It once stamped a plain Folder, which containment then refused,
    // so nothing could ever be filed into such a folder through the API (found by the Licensing demo, 2026-10-07).
    [Fact]
    public async Task A_modules_exclusive_folder_receives_an_untyped_child_that_becomes_an_admitted_item()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            var binder = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Binder {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.BinderMaskId })).GetProperty("id").GetGuid();

            var child = (await TestJson.Post(rig.Owner, $"/api/documents/{binder}/children", new { name = "Medical" })).GetProperty("id").GetGuid();
            await TestJson.Put(rig.Owner, $"/api/documents/{child}/mask", new { maskId = SimplArchive.TestModule.TestModule.CertificateMaskId });
            using (var scope = _factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.CurrentTenantAccessor>().TenantId = rig.TenantId;
                var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
                var maskId = await db.Documents.Where(d => d.Id == child)
                    .Join(db.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => v.MaskId).SingleAsync();
                Assert.Equal(SimplArchive.TestModule.TestModule.CertificateMaskId, maskId);
            }

            var refused = await rig.Owner.PostAsJsonAsync($"/api/documents/{binder}/children",
                new { name = "Sub", maskId = SimplArchive.ModuleAbi.CoreMaskIds.Folder });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    // ABI 1.10 (ADR 0902): InTransactionAsync makes a module's own act atomic. A field write and a queued notice inside
    // it both vanish when the act throws, and both stand when it succeeds.
    [Fact]
    public async Task A_modules_act_in_a_transaction_leaves_nothing_behind_when_it_throws()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            var watcherEmail = $"watcher-{Guid.NewGuid():N}@e2e.local";
            await _factory.SeedUserAsync(rig.TenantId, watcherEmail, "watcher-1234", "Watcher");
            using var watcher = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(watcherEmail, "watcher-1234"));
            var dossierId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Dossier {Guid.NewGuid():N}", maskId = SimplArchive.TestModule.TestModule.DossierMaskId })).GetProperty("id").GetGuid();

            async Task<string?> MentorAsync() =>
                (await TestJson.Get(rig.Admin, $"/api/documents/{dossierId}/index-data")).GetProperty("fields").EnumerateArray()
                    .Where(f => f.GetProperty("fieldName").GetString() == "Mentor")
                    .Select(f => f.GetProperty("values").EnumerateArray().Select(v => v.GetString()).FirstOrDefault())
                    .FirstOrDefault();
            async Task<int> NoticesAsync() =>
                (await TestJson.Get(watcher, "/api/notifications")).GetProperty("notifications").EnumerateArray()
                    .Count(n => n.GetProperty("title").GetString() == "Mentor changed");

            var refused = await rig.Admin.PostAsJsonAsync($"/api/test-module/documents/{dossierId}/atomic?fail=true",
                new { mentor = "rolled-back@e2e.local", notify = watcherEmail });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Null(await MentorAsync());
            Assert.Equal(0, await NoticesAsync());

            Assert.Equal(HttpStatusCode.NoContent, (await rig.Admin.PostAsJsonAsync($"/api/test-module/documents/{dossierId}/atomic?fail=false",
                new { mentor = "kept@e2e.local", notify = watcherEmail })).StatusCode);
            Assert.Equal("kept@e2e.local", await MentorAsync());
            Assert.Equal(1, await NoticesAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }
}
