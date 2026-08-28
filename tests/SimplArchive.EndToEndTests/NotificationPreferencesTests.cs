using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising notification email preferences (ADR "Notification
// preferences"): a User reads their defaults (all mutable types emailed), mutes a type, reads it back, can't
// mute a deadline/compliance escalation, and a ServiceAccount has no intray.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class NotificationPreferencesTests
{
    private readonly E2EApiFactory _factory;

    public NotificationPreferencesTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Read_defaults_mute_a_type_and_reject_an_escalation()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"prefs-{Guid.NewGuid():N}@e2e.local";
        const string password = "prefs1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Prefs User");
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Defaults: the mutable types, all emailed (ADR "Notification preferences" + "Document subscriptions").
        var defaults = (await TestJson.Get(user, "/api/notifications/preferences")).GetProperty("preferences").EnumerateArray().ToArray();
        Assert.Equal(SimplArchive.Domain.Notifications.NotificationTypePolicy.Mutable.Count, defaults.Length);
        Assert.All(defaults, p => Assert.True(p.GetProperty("emailEnabled").GetBoolean()));

        // Mute ChatMessagePosted (type 4); the rest stay on.
        var body = new
        {
            preferences = defaults.Select(p => new
            {
                type = p.GetProperty("type").GetInt32(),
                emailEnabled = p.GetProperty("type").GetInt32() != 4,
            }).ToArray(),
        };
        (await user.PutAsJsonAsync("/api/notifications/preferences", body)).EnsureSuccessStatusCode();

        // Read back: ChatMessagePosted is off, everything else on.
        var after = (await TestJson.Get(user, "/api/notifications/preferences")).GetProperty("preferences").EnumerateArray().ToArray();
        Assert.False(after.Single(p => p.GetProperty("type").GetInt32() == 4).GetProperty("emailEnabled").GetBoolean());
        Assert.All(after.Where(p => p.GetProperty("type").GetInt32() != 4), p => Assert.True(p.GetProperty("emailEnabled").GetBoolean()));

        // A deadline/compliance escalation (ReviewOverdue = 7) can't be muted.
        var bad = await user.PutAsJsonAsync("/api/notifications/preferences", new { preferences = new[] { new { type = 7, emailEnabled = false } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("INVALID_NOTIFICATION_PREFERENCE", await ErrorCodeAsync(bad));

        // A ServiceAccount has no intray → no preferences.
        using var service = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/api/notifications/preferences")).StatusCode);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
    }
}
