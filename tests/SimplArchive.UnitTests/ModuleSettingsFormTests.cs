using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

/// <summary>
/// What a module-settings form sends on save (ADR 0772) — shared by both clients, so the rule is pinned once.
/// </summary>
/// <remarks>
/// The first case is the one that matters. A secret's value never crosses the wire back, so its box is empty
/// even when a credential is configured; sending that empty box would CLEAR it. The form would report a
/// successful save, look correct on reload, and the destruction would only surface at the next call that
/// needed the credential — possibly days later, in a scheduled fetch nobody was watching.
/// </remarks>
public class ModuleSettingsFormTests
{
    [Fact]
    public void An_untouched_secret_is_omitted_rather_than_cleared()
    {
        var values = ModuleSettingsForm.ValuesToSend(
        [
            new("endpoint", IsSecret: false, "https://example.test"),
            new("apiSecret", IsSecret: true, string.Empty),   // configured, box left alone
        ]);

        Assert.Equal("https://example.test", values["endpoint"]);
        Assert.False(values.ContainsKey("apiSecret"),
            "an untouched secret must not be sent — the server's merge would read an empty value as 'clear it'");
    }

    [Fact]
    public void A_typed_secret_is_sent()
    {
        var values = ModuleSettingsForm.ValuesToSend([new("apiSecret", IsSecret: true, "rotated")]);

        Assert.Equal("rotated", values["apiSecret"]);
    }

    [Fact]
    public void An_emptied_plain_field_is_cleared()
    {
        // Not a secret, so an empty box is a real intention: the administrator removed the value.
        var values = ModuleSettingsForm.ValuesToSend([new("endpoint", IsSecret: false, string.Empty)]);

        Assert.True(values.ContainsKey("endpoint"));
        Assert.Null(values["endpoint"]);
    }

    [Fact]
    public void Nothing_typed_at_all_sends_nothing()
    {
        // A form opened and saved without edits must be a no-op, not a mass clear.
        var values = ModuleSettingsForm.ValuesToSend(
        [
            new("apiSecret", IsSecret: true, string.Empty),
            new("otherSecret", IsSecret: true, string.Empty),
        ]);

        Assert.Empty(values);
    }
}
