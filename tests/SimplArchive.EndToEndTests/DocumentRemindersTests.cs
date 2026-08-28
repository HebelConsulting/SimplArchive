using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising document reminders (ADR "Document reminders"): a user
// sets a reminder targeting a colleague; the sweep fires it into the target's intray on the due date; cancel
// removes a pending reminder; a ServiceAccount can't set one; a target who can't see the document is rejected.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentRemindersTests
{
    private readonly E2EApiFactory _factory;

    public DocumentRemindersTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Set_a_reminder_for_a_colleague_it_fires_on_the_sweep_and_can_be_cancelled()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "remind-1234";
        // Setter (A) and target (B) can both see everything (tenant admins); C cannot see the document.
        var aEmail = $"rem-a-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, aEmail, password, "Setter A");
        await _factory.GrantTenantAdminAsync(aEmail);
        using var setter = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(aEmail, password));

        var bEmail = $"rem-b-{Guid.NewGuid():N}@e2e.local";
        var bId = await _factory.SeedUserAsync(tenantId, bEmail, password, "Target B");
        await _factory.GrantTenantAdminAsync(bEmail);
        using var target = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bEmail, password));

        var cEmail = $"rem-c-{Guid.NewGuid():N}@e2e.local";
        var cId = await _factory.SeedUserAsync(tenantId, cEmail, password, "Outsider C");

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Rem {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "rem-doc" })).GetProperty("id").GetGuid();

        // A ServiceAccount can't set a reminder (no in-app intray).
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 0 })).StatusCode);

        // Targeting a user who can't see the document is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await setter.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 0, targetUserId = cId })).StatusCode);

        // A past due date is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await setter.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddMinutes(-5), recurrence = 0 })).StatusCode);

        // A sets a reminder targeting B, with a note.
        var created = await TestJson.Post(setter, $"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), note = "Renewal due", recurrence = 0, targetUserId = bId });
        var reminderId = created.GetProperty("id").GetGuid();
        Assert.Equal(bId, created.GetProperty("targetUserId").GetGuid());

        // Both A (creator) and B (target) see it in the document's reminder list.
        Assert.Contains((await TestJson.Get(setter, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == reminderId);
        Assert.Contains((await TestJson.Get(target, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == reminderId);

        // Back-date it and run the sweep → B gets a DocumentReminder notification carrying the note.
        await _factory.BackdateReminderAsync(reminderId);
        Assert.True(await _factory.RunReminderSweepAsync() >= 1);
        var bIntray = (await TestJson.Get(target, "/api/notifications")).GetProperty("notifications").EnumerateArray().ToList();
        var fired = bIntray.Single(n => n.GetProperty("type").GetString() == "DocumentReminder" && n.GetProperty("documentId").GetGuid() == docId);
        Assert.Contains("Renewal due", fired.GetProperty("body").GetString());

        // The one-shot is done — it no longer appears as pending.
        Assert.DoesNotContain((await TestJson.Get(target, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == reminderId);

        // Cancel path: A sets another reminder for itself, then cancels it.
        var second = await TestJson.Post(setter, $"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(2), recurrence = 2 });
        var secondId = second.GetProperty("id").GetGuid();
        (await setter.DeleteAsync($"/api/documents/{docId}/reminders/{secondId}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await TestJson.Get(setter, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == secondId);
    }
}
