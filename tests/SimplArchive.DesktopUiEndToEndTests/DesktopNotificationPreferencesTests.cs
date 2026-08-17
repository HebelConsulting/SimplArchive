using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of notification preferences (ADR "Notification preferences"): the real DesktopClient
// SimplArchiveApiClient reads the defaults, mutes a type, reads it back, then restores — verifying the
// api-client wiring end to end (the dialog is exercised by the account menu). Restores the demo admin's
// preferences at the end so the shared demo user is left untouched.
[Collection(UiCollection.Name)]
public class DesktopNotificationPreferencesTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopNotificationPreferencesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Read_defaults_mute_a_type_read_back_and_restore()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        try
        {
            var defaults = await client.Profile.GetNotificationPreferencesAsync();
            // The mutable-type set grows over time; assert a lower bound (this project can't reference the Domain
            // policy — the Api reference is ReferenceOutputAssembly=false). The E2E test checks the exact count.
            Assert.True(defaults.Count >= 6, $"expected at least the 6 original mutable types, got {defaults.Count}");
            Assert.All(defaults, p => Assert.True(p.EmailEnabled));

            // Mute ChatMessagePosted (type 4), keep the rest on.
            await client.Profile.SetNotificationPreferencesAsync(
                defaults.Select(p => p with { EmailEnabled = p.Type != 4 }));

            var after = await client.Profile.GetNotificationPreferencesAsync();
            Assert.False(after.Single(p => p.Type == 4).EmailEnabled);
            Assert.All(after.Where(p => p.Type != 4), p => Assert.True(p.EmailEnabled));
        }
        finally
        {
            // Restore the shared demo admin's preferences to all-on.
            var restore = (await client.Profile.GetNotificationPreferencesAsync()).Select(p => p with { EmailEnabled = true });
            await client.Profile.SetNotificationPreferencesAsync(restore);
        }
    }
}
