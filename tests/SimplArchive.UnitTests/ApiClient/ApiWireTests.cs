using System.Net;
using System.Text.Json;
using SimplArchive.ApiClient;
using SimplArchive.Localization;

namespace SimplArchive.UnitTests.ApiClient;

// The wire helpers every area client shares (ADR 0917): a refusal reaches the reader as THEIR language, from the
// machine-readable error code, never as the server's English detail (issue #424).
public sealed class ApiWireTests
{
    private const string Problem = "application/problem+json";

    [Fact]
    public async Task A_success_is_not_a_problem() =>
        await ApiWire.ThrowIfProblemAsync(new HttpResponseMessage(HttpStatusCode.NoContent), "fallback", default);

    [Fact]
    public async Task A_refusal_is_told_from_its_error_code_never_from_the_servers_detail()
    {
        var response = FakeHandler.Json("""{"errorCode":"ETAG_MISMATCH","detail":"English text the reader must not see"}""", HttpStatusCode.PreconditionFailed, Problem);

        var refused = await Assert.ThrowsAsync<ApiActionException>(() => ApiWire.ThrowIfProblemAsync(response, "fallback", default));

        Assert.Equal(ApiErrorText.For("ETAG_MISMATCH"), refused.Message);
        Assert.DoesNotContain("English text", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_without_a_problem_body_says_what_the_caller_was_doing()
    {
        var proxyPage = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") };

        var refused = await Assert.ThrowsAsync<ApiActionException>(() => ApiWire.ThrowIfProblemAsync(proxyPage, "Could not save.", default));

        Assert.Equal("Could not save.", refused.Message);
    }

    [Fact]
    public async Task The_error_message_reads_the_code_too_and_falls_back_without_one()
    {
        Assert.Equal(ApiErrorText.For("ETAG_MISMATCH"),
            await ApiWire.ErrorMessageAsync(FakeHandler.Json("""{"errorCode":"ETAG_MISMATCH"}""", HttpStatusCode.PreconditionFailed, Problem), "fallback"));
        Assert.Equal("fallback",
            await ApiWire.ErrorMessageAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("oops") }, "fallback"));
    }

    [Fact]
    public void An_optional_string_is_a_string_or_nothing()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""{"name":"Invoice","count":3,"none":null}""");

        Assert.Equal("Invoice", ApiWire.StrOrNull(json, "name"));
        Assert.Null(ApiWire.StrOrNull(json, "count"));
        Assert.Null(ApiWire.StrOrNull(json, "none"));
        Assert.Null(ApiWire.StrOrNull(json, "missing"));
    }

    [Fact]
    public void A_resources_links_are_read_with_their_methods()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            {"links":[{"rel":"self","href":"/api/documents/1","method":"GET"},{"rel":"children","href":"/api/documents/1/children","method":"POST"}]}
            """);

        var links = ApiWire.ParseLinks(json);

        Assert.NotNull(links);
        Assert.Equal("api/documents/1/children", links!.Href("children"));   // relative to the client's base, as every href is kept
        Assert.Null(links.Href("delete"));   // a missing rel means "not available to you" (ADR 0543)
    }

    [Fact]
    public void A_resource_without_links_has_none()
    {
        var links = ApiWire.ParseLinks(JsonSerializer.Deserialize<JsonElement>("""{"id":1}"""));

        Assert.True(links is null || links.Href("self") is null);
    }
}
