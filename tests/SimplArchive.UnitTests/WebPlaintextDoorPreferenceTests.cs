using SimplArchive.Client.Hypermedia;

namespace SimplArchive.UnitTests;

/// <summary>
/// The web client's fixed preference between the two content doors an enveloping tenant offers (ADR 0865).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="ContentDoorsTests"/>: that one pins what the SERVER advertises, this pins
/// which of the pair this client follows. Both halves are needed, and the reason is the standing lesson that
/// advertising and accepting are two lists — fixing one is worse than fixing neither.
/// </para>
/// <para>
/// Tests <see cref="Links.PlaintextFirst"/> itself rather than a re-statement of the rule, which is why the
/// rule lives in <c>Links</c> instead of as a private helper in <c>DetailLoader</c>: a test that re-derives
/// the chain passes against a copy and says nothing about the code that ships.
/// </para>
/// </remarks>
public class WebPlaintextDoorPreferenceTests
{
    private static List<LinkResponse> Links_(params (string Rel, string Href)[] links) =>
        [.. links.Select(l => new LinkResponse { Rel = l.Rel, Href = l.Href, Method = "GET" })];

    [Fact]
    public void On_a_permissive_tenant_the_browser_takes_the_PLAINTEXT_address()
    {
        // Both pairs advertised — the tenant delivers envelopes AND its doors serve. A browser cannot open a
        // CMS envelope (ADR 0830) and cannot even fetch that route (it authorizes by header while the content
        // path carries no bearer), which is what produced five 401s and a blank preview pane on the live
        // stack. So it must take the presigned one.
        var links = Links_(
            ("download", "/api/documents/d/versions/v/enveloped-content"),
            ("preview", "/api/documents/d/versions/v/enveloped-content?inline=true"),
            ("plain-download", "http://storage.invalid/blob?sig=1"),
            ("plain-preview", "http://storage.invalid/blob?sig=2&inline"));

        Assert.Equal("http://storage.invalid/blob?sig=1", Links.PlaintextFirst(links, "download"));
        Assert.Equal("http://storage.invalid/blob?sig=2&inline", Links.PlaintextFirst(links, "preview"));
    }

    [Fact]
    public void On_an_ordinary_tenant_nothing_changes()
    {
        // No `plain-*` because there is only one door, so the bare rels ARE the plaintext ones. This is the
        // overwhelming majority of installations, and the assertion that the change is invisible to them.
        var links = Links_(
            ("download", "http://storage.invalid/blob?sig=1"),
            ("preview", "http://storage.invalid/blob?sig=2&inline"));

        Assert.Equal("http://storage.invalid/blob?sig=1", Links.PlaintextFirst(links, "download"));
        Assert.Equal("http://storage.invalid/blob?sig=2&inline", Links.PlaintextFirst(links, "preview"));
    }

    [Fact]
    public void On_a_strict_tenant_the_fallback_is_the_ENVELOPE_so_the_pane_can_say_why()
    {
        // The deliberate half. Strict advertises no plaintext pair, so this answers the enveloped address —
        // which the pane cannot render, and that is correct: `contentIsEnveloped` then turns the absence into
        // "use the desktop client" rather than "no preview available". The two are indistinguishable from the
        // links alone, which is the whole reason the server states the tier.
        var links = Links_(
            ("download", "/api/documents/d/versions/v/enveloped-content"),
            ("preview", "/api/documents/d/versions/v/enveloped-content?inline=true"));

        Assert.Equal("api/documents/d/versions/v/enveloped-content", Links.PlaintextFirst(links, "download"));
    }

    [Fact]
    public void A_tenant_that_serves_no_content_at_all_answers_null_rather_than_a_composed_path()
    {
        // Strict with no reader certificate: neither pair is advertised. A missing rel means "not available to
        // you, here, now" (ADR 0543), so the honest answer is null and the caller hides the affordance — never
        // a guessed address, which is what would turn an absent rel into a 404 nobody can interpret.
        Assert.Null(Links.PlaintextFirst(Links_(("self", "/api/documents/d/versions/v")), "download"));
        Assert.Null(Links.PlaintextFirst(null, "preview"));
    }
}
