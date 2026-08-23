using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A Mailbox's address claims (#703 PR 2): who may write the list, which claims it may carry, and what the
// audit trail says. Every row of the issue's decisions log has its assertion here, because each one is a rule
// about mail silently going somewhere else — the kind of defect nobody reports because nobody sees it.
//
// The actors are ROUTING USERS operating on their OWN personal mailboxes, not a tenant admin on somebody
// else's — deliberately: ADR 0670 stops the admin bypass at a foreign personal space's edge, so an admin
// holds no CanEditIndexData there and would be refused before the claims logic ever ran. The routing right
// composes with ordinary ACL rights; it does not replace them.
[Collection(E2ECollection.Name)]
public class MailboxAddressClaimTests
{
    private readonly E2EApiFactory _factory;

    public MailboxAddressClaimTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Mailbox(Guid DocumentId, Guid FieldId, HttpClient Client, Guid UserId, string OwnerEmail);

    /// <summary>A user with their personal mailbox materialised, and the address field's definition id.</summary>
    private async Task<Mailbox> PersonalMailboxAsync(Guid tenantId, string prefix, bool canRoute)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "u-1234", prefix,
            canViewAuditLog: true, canManageMailRouting: canRoute);
        var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var personalId = (await TestJson.Post(client, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();

        // The mailbox materialises when IMAP access is provisioned OR when ingress mail first delivers to
        // this user — not with the bare personal repository. This test uses the first trigger (the same one
        // ImapEndpointTests uses); LMTP delivery exercises the second.
        await TestJson.Post(client, "/api/me/imap-access", new { });
        var mailboxId = (await TestJson.Get(client, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Mailbox")
            .GetProperty("id").GetGuid();

        var maskId = (await TestJson.Get(client, $"/api/documents/{mailboxId}/mask")).GetProperty("maskId").GetGuid();
        var field = (await TestJson.Get(client, $"/api/masks/{maskId}")).GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "eMail Addresses");

        // The field says what it is: a list of addresses whose editing is routing-gated. Asserted here, on the
        // shared setup, because BOTH clients key their editor and their read-only rendering off these flags.
        Assert.True(field.GetProperty("isList").GetBoolean());
        Assert.True(field.GetProperty("requiresMailRouting").GetBoolean());
        Assert.Equal("EmailAddress", field.GetProperty("dataType").GetString());

        return new Mailbox(mailboxId, field.GetProperty("id").GetGuid(), client, userId, email);
    }

    private static object Claims(Guid fieldId, bool confirm = false, params string[] addresses) =>
        new { fields = new[] { new { fieldDefinitionId = fieldId, values = addresses } }, confirmDuplicateClaims = confirm };

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()!;

    [Fact]
    public async Task Claims_are_written_by_a_routing_holder_and_audited_in_both_directions()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var box = await PersonalMailboxAsync(tenantId, "claims", canRoute: true);
        using var client = box.Client;

        await TestJson.Put(client, $"/api/documents/{box.DocumentId}/index-data",
            Claims(box.FieldId, confirm: false, "events@corp.test", "veranstaltungen@corp.test"));

        var stored = (await TestJson.Get(client, $"/api/documents/{box.DocumentId}/index-data"))
            .GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == "eMail Addresses")
            .GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["events@corp.test", "veranstaltungen@corp.test"], stored);

        // Dropping one is a RELEASE, and both directions are audited: an address that quietly starts or stops
        // receiving is exactly what an auditor is asked to reconstruct.
        await TestJson.Put(client, $"/api/documents/{box.DocumentId}/index-data",
            Claims(box.FieldId, confirm: false, "events@corp.test"));

        var actions = (await TestJson.Get(client, "/api/audit-events?limit=50")).GetProperty("events").EnumerateArray()
            .Where(e => e.TryGetProperty("targetId", out var t) && t.ValueKind == JsonValueKind.String && t.GetGuid() == box.DocumentId)
            .Select(e => (Action: e.GetProperty("action").GetString(), Details: e.GetProperty("details").GetString()))
            .ToList();
        Assert.Contains(actions, e => e.Action == "Mailbox.AddressClaimed" && e.Details!.Contains("events@corp.test"));
        Assert.Contains(actions, e => e.Action == "Mailbox.AddressReleased" && e.Details!.Contains("veranstaltungen@corp.test"));
    }

    [Fact]
    public async Task Without_the_right_a_change_is_403_but_a_no_op_resubmission_is_not()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var box = await PersonalMailboxAsync(tenantId, "norights", canRoute: false);
        using var owner = box.Client;

        // The owner holds every ACL right on their own personal mailbox — and still may not claim an address:
        // the list decides where the TENANT's mail goes, which CanEditIndexData was never about.
        var refused = await owner.PutAsJsonAsync($"/api/documents/{box.DocumentId}/index-data",
            Claims(box.FieldId, confirm: false, "ceo@corp.test"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MAIL_ROUTING_RIGHT_REQUIRED", await ErrorCodeAsync(refused));

        // The gate is on CHANGE, not on presence: the PUT replaces the whole field set, so a caller editing
        // anything else on a mailbox resubmits this field too — an unchanged list must pass, or every field
        // on every mailbox is locked to routing admins.
        var unchanged = await owner.PutAsJsonAsync($"/api/documents/{box.DocumentId}/index-data",
            new { fields = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }

    [Fact]
    public async Task A_second_claim_conflicts_until_confirmed_as_a_fan_out()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var first = await PersonalMailboxAsync(tenantId, "first", canRoute: true);
        var second = await PersonalMailboxAsync(tenantId, "second", canRoute: true);
        using var a = first.Client;
        using var b = second.Client;

        await TestJson.Put(a, $"/api/documents/{first.DocumentId}/index-data",
            Claims(first.FieldId, confirm: false, "sales@corp.test"));

        // Case-insensitive on purpose (the NormalizedEmail precedent): SALES@ and sales@ are one address.
        var conflict = await b.PutAsJsonAsync($"/api/documents/{second.DocumentId}/index-data",
            Claims(second.FieldId, confirm: false, "SALES@corp.test"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // Read ONCE — the in-process TestServer's response stream does not survive a second read.
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DUPLICATE_ADDRESS_CLAIM", problem.GetProperty("errorCode").GetString());

        // The refusal NAMES the claiming mailbox — as DATA (the claimedBy extension), because that is what
        // both clients compose their localized question from; the English detail is a human fallback.
        Assert.Equal("My Mailbox", problem.GetProperty("claimedBy").GetString());
        Assert.Contains("My Mailbox", problem.GetProperty("detail").GetString()!);

        // The explicit retry makes delivery fan out — a feature, not only a hazard — and the override is its
        // own audit action, because "someone decided two mailboxes receive this address" is the fact an
        // auditor hunts for.
        await TestJson.Put(b, $"/api/documents/{second.DocumentId}/index-data",
            Claims(second.FieldId, confirm: true, "SALES@corp.test"));

        var audit = (await TestJson.Get(b, "/api/audit-events?limit=50")).GetProperty("events").EnumerateArray();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "Mailbox.DuplicateClaimConfirmed");
    }

    [Fact]
    public async Task The_same_address_twice_in_one_list_is_refused_as_invalid()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var box = await PersonalMailboxAsync(tenantId, "twice", canRoute: true);
        using var client = box.Client;

        // Case-insensitively the same address, twice, in ONE submission — not a duplicate CLAIM (that names
        // another mailbox) but a malformed list, so it wears the field-validation code.
        var refused = await client.PutAsJsonAsync($"/api/documents/{box.DocumentId}/index-data",
            Claims(box.FieldId, confirm: false, "sales@corp.test", "SALES@corp.test"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("FIELD_VALUE_INVALID", await ErrorCodeAsync(refused));
    }

    [Fact]
    public async Task A_users_personal_address_is_never_claimable_even_confirmed()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var box = await PersonalMailboxAsync(tenantId, "victim", canRoute: true);
        using var client = box.Client;

        foreach (var confirm in new[] { false, true })
        {
            // The confirm=true arm is the whole point: the duplicate override must NOT double as an override
            // here — claiming a person's address silently diverts their mail, and no flag makes that ok.
            var refused = await client.PutAsJsonAsync($"/api/documents/{box.DocumentId}/index-data",
                Claims(box.FieldId, confirm, box.OwnerEmail.ToUpperInvariant()));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("USER_ADDRESS_CLAIM_NOT_ALLOWED", await ErrorCodeAsync(refused));
        }
    }

    [Fact]
    public async Task Deleting_a_mailbox_needs_the_routing_right_and_a_standing_folder_refuses_cleanly()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var plain = await PersonalMailboxAsync(tenantId, "plain", canRoute: false);
        var router = await PersonalMailboxAsync(tenantId, "router", canRoute: true);
        using var owner = plain.Client;
        using var routing = router.Client;

        // The plain owner holds CanDelete on their own mailbox — deletion is still refused, because it is the
        // moment the mailbox's addresses stop receiving (owner-decided 2026-08-23). The routing gate fires
        // FIRST, so the caller learns which right is missing rather than tripping the invariant below.
        var etag = (await owner.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{plain.DocumentId}"))).Headers.ETag!.ToString();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{plain.DocumentId}");
        del.Headers.TryAddWithoutValidation("If-Match", etag);
        var refused = await owner.SendAsync(del);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MAIL_ROUTING_RIGHT_REQUIRED", await ErrorCodeAsync(refused));

        // A ROUTING holder passes the gate — and then meets the #596 standing-folder invariant, because the
        // only mailboxes that exist today are personal ones, and a personal space's "My Mailbox" is structural
        // (the calendar, contacts and mail views resolve against it). This used to surface as a bare 500; it
        // is a typed 409 now, found live by this very test.
        //
        // The gate's SUCCESS arm — actually deleting and restoring a mailbox — becomes reachable with #703
        // PR 4's department mailboxes, and its test ships there with them.
        var routerEtag = (await routing.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/documents/{router.DocumentId}"))).Headers.ETag!.ToString();
        var routerDel = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{router.DocumentId}");
        routerDel.Headers.TryAddWithoutValidation("If-Match", routerEtag);
        var structural = await routing.SendAsync(routerDel);
        Assert.Equal(HttpStatusCode.Conflict, structural.StatusCode);
        Assert.Equal("PERSONAL_SPACE_STRUCTURE", await ErrorCodeAsync(structural));
    }
}
