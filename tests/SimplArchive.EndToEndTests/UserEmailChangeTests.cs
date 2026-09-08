using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end for changing a user's login address (#465). The address IS the login identifier, so this is a
// security-relevant mutation rather than a profile tweak: it is gated on CanManageUsers, refused where the
// deployment forbids it at all, and recorded in the audit log.
//
// The persistence trap this pins is ADR 0150's: `User.Email`'s setter derives `NormalizedEmail`, and the unique
// index is on (TenantId, NormalizedEmail) — so a collision must be a 409 and the normalized column must follow
// the change rather than being written directly. The proof of the second half is that the user can LOG IN under
// the new address afterwards, which goes through the normalized lookup.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class UserEmailChangeTests
{
    private readonly E2EApiFactory _factory;

    public UserEmailChangeTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_administrator_follows_the_rel_and_the_user_logs_in_under_the_new_address()
    {
        var (admin, tenantId) = await AdminAsync();

        var oldEmail = $"email-before-{Guid.NewGuid():N}@e2e.local";
        const string password = "email1234";
        var userId = await _factory.SeedUserAsync(tenantId, oldEmail, password, "Renamed Person");

        // The address comes from the row, never composed (ADR 0543) — which is also what the clients do.
        var row = await FindUserAsync(admin, userId);
        Assert.Equal(oldEmail, row.GetProperty("email").GetString());
        var href = Rel(row, "email");
        Assert.NotNull(href);

        var newEmail = $"email-after-{Guid.NewGuid():N}@e2e.local";
        var response = await admin.PutAsJsonAsync(href!, new { email = newEmail });
        response.EnsureSuccessStatusCode();

        var updated = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(newEmail, updated.GetProperty("email").GetString());

        // The normalized column moved with it: the login flow resolves a user by NormalizedEmail, so a token
        // under the new address is the assertion that the setter did its job.
        var token = await _factory.GetUserTokenAsync(newEmail, password);
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task A_second_user_cannot_take_an_address_already_in_the_tenant()
    {
        var (admin, tenantId) = await AdminAsync();

        var taken = $"email-taken-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, taken, "email1234", "First Holder");
        var moverId = await _factory.SeedUserAsync(tenantId, $"email-mover-{Guid.NewGuid():N}@e2e.local", "email1234", "Would-be Twin");

        var href = Rel(await FindUserAsync(admin, moverId), "email")!;

        // Uppercased: the index is on the NORMALIZED column, so a differently-cased spelling is the same address.
        var conflict = await admin.PutAsJsonAsync(href, new { email = taken.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task An_address_that_is_not_one_is_refused()
    {
        var (admin, tenantId) = await AdminAsync();
        var userId = await _factory.SeedUserAsync(tenantId, $"email-invalid-{Guid.NewGuid():N}@e2e.local", "email1234", "Fussy");

        var href = Rel(await FindUserAsync(admin, userId), "email")!;

        foreach (var nonsense in new[] { "", "   ", "not-an-address" })
        {
            var refused = await admin.PutAsJsonAsync(href, new { email = nonsense });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    // The right the whole action hangs on. A caller without CanManageUsers cannot even LIST users, so it never
    // sees the rel — this asserts the server refuses the call regardless, which is what makes the rel an
    // affordance rather than the enforcement.
    [Fact]
    public async Task A_caller_without_the_right_is_refused_even_holding_the_address()
    {
        var (admin, tenantId) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync(tenantId, $"email-target-{Guid.NewGuid():N}@e2e.local", "email1234", "Target");
        var href = Rel(await FindUserAsync(admin, targetId), "email")!;

        var plainEmail = $"email-plain-{Guid.NewGuid():N}@e2e.local";
        const string password = "email1234";
        await _factory.SeedUserAsync(tenantId, plainEmail, password, "Plain Member");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, password));

        var refused = await plain.PutAsJsonAsync(href, new { email = $"email-nope-{Guid.NewGuid():N}@e2e.local" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // Changing how someone authenticates is exactly the kind of mutation the audit trail exists for, and the
    // entry has to carry BOTH addresses — "the address changed" is not answerable after the fact otherwise.
    [Fact]
    public async Task The_change_is_recorded_with_both_addresses()
    {
        var (admin, tenantId) = await AdminAsync();

        var oldEmail = $"email-audit-before-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, oldEmail, "email1234", "Audited Person");
        var newEmail = $"email-audit-after-{Guid.NewGuid():N}@e2e.local";

        var href = Rel(await FindUserAsync(admin, userId), "email")!;
        (await admin.PutAsJsonAsync(href, new { email = newEmail })).EnsureSuccessStatusCode();

        var events = (await GetJson(admin, "/api/audit-events?action=User.EmailChanged&limit=200")).GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetGuid() == userId)
            .ToList();

        var recorded = Assert.Single(events);
        var detail = recorded.GetProperty("details").GetString() ?? string.Empty;
        Assert.Contains(oldEmail, detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(newEmail, detail, StringComparison.OrdinalIgnoreCase);
    }

    // Re-sending the address a user already has is a no-op rather than an error — and must NOT produce an audit
    // entry, or a client that saves without changing anything pollutes the trail.
    [Fact]
    public async Task Setting_the_same_address_records_nothing()
    {
        var (admin, tenantId) = await AdminAsync();

        var email = $"email-idem-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "email1234", "Unchanged");
        var href = Rel(await FindUserAsync(admin, userId), "email")!;

        (await admin.PutAsJsonAsync(href, new { email })).EnsureSuccessStatusCode();

        var events = (await GetJson(admin, "/api/audit-events?action=User.EmailChanged&limit=200")).GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetGuid() == userId);

        Assert.Empty(events);
    }

    /// <summary>An administrator who may manage users AND read the audit log, plus their tenant.</summary>
    private async Task<(HttpClient Admin, Guid TenantId)> AdminAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"email-admin-{Guid.NewGuid():N}@e2e.local";
        const string password = "email1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Email Admin", canManageUsers: true, canViewAuditLog: true);
        return (_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password)), tenantId);
    }

    /// <summary>The user's row as the LISTING advertises it — the shape both clients hold when they act.</summary>
    private static async Task<JsonElement> FindUserAsync(HttpClient admin, Guid userId)
    {
        var url = "/api/users?limit=200";
        while (true)
        {
            var page = await GetJson(admin, url);
            foreach (var user in page.GetProperty("users").EnumerateArray())
            {
                if (user.GetProperty("id").GetGuid() == userId)
                {
                    return user;
                }
            }

            url = Rel(page, "next") ?? throw new InvalidOperationException($"user {userId} is not in the listing");
        }
    }

    private static string? Rel(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
            ? link.GetProperty("href").GetString()
            : null;

    private static async Task<JsonElement> GetJson(HttpClient api, string url)
    {
        var response = await api.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
