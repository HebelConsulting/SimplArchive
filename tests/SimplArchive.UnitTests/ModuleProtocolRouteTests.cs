using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// ABI 1.7 (ADR 0898): which root prefixes a module may claim, where its controllers may route, and how a protocol
// credential is read and parsed.
public class ModuleProtocolRouteTests
{
    [Route("nuget/v3")]
    public sealed class FeedController : ControllerBase
    {
        [HttpGet("index.json")]
        public IActionResult Index() => Ok();
    }

    [Route("api/modules/m")]
    public sealed class ApiController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok();

        [HttpGet("~/connect/token")]
        public IActionResult Hijack() => Ok();
    }

    private static readonly Dictionary<string, string> NoneClaimed = new();

    [Fact]
    public void A_module_may_claim_an_allowlisted_prefix_and_route_under_it() =>
        Assert.Null(ModuleRoutes.Refusal("m", ["nuget"], NoneClaimed, [typeof(FeedController)]));

    [Fact]
    public void A_prefix_off_the_allowlist_is_refused_by_name()
    {
        var refusal = ModuleRoutes.Refusal("m", ["connect"], NoneClaimed, []);
        Assert.Contains("'connect'", refusal, StringComparison.Ordinal);
        Assert.Contains("allowlist", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prefix_another_module_holds_is_refused_naming_the_holder() =>
        Assert.Contains("module first already holds",
            ModuleRoutes.Refusal("second", ["nuget"], new Dictionary<string, string> { ["nuget"] = "first" }, []), StringComparison.Ordinal);

    // Without the claim, the same controller routes outside api/: refused.
    [Fact]
    public void A_root_route_without_the_claim_is_refused() =>
        Assert.Contains("'nuget/v3/index.json'", ModuleRoutes.Refusal("m", [], NoneClaimed, [typeof(FeedController)]), StringComparison.Ordinal);

    // An action's absolute template escapes its controller's api/ route: the check sees it.
    [Fact]
    public void An_absolute_action_route_outside_api_is_refused() =>
        Assert.Contains("'connect/token'", ModuleRoutes.Refusal("m", ["nuget"], NoneClaimed, [typeof(ApiController)]), StringComparison.Ordinal);

    [Fact]
    public void Templates_combine_as_mvc_combines_them() =>
        Assert.Equal(["api/modules/m", "connect/token"], ModuleRoutes.RouteTemplates([typeof(ApiController)]));

    [Fact]
    public void The_credential_format_round_trips_and_refuses_anything_else()
    {
        var tenant = Guid.NewGuid();
        Assert.True(ModuleCredentialFormat.TryParse(ModuleCredentialFormat.Compose(tenant, "s3cr_et"), out var parsedTenant, out var secret));
        Assert.Equal((tenant, "s3cr_et"), (parsedTenant, secret));

        foreach (var bad in new[] { null, "", "open-sesame", "sa_", $"sa_{tenant:N}_", $"sa_{tenant:N}", "sa_not-a-guid_x", $"xa_{tenant:N}_x" })
        {
            Assert.False(ModuleCredentialFormat.TryParse(bad, out _, out _));
        }
    }

    [Theory]
    [InlineData("nuget", "/nuget", true)]
    [InlineData("nuget", "/nuget/v3/index.json", true)]
    [InlineData("nuget/v3", "/NuGet/v3/x", true)]
    [InlineData("nuget", "/nugetx/v3", false)]
    [InlineData("", "/nuget", false)]
    public void A_prefix_covers_whole_segments_only(string prefix, string path, bool covers) =>
        Assert.Equal(covers, ModuleCredentialMiddleware.Covers(prefix, path));

    [Fact]
    public void The_basic_password_is_the_credential_and_the_user_name_is_ignored()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("anyone:sa_x:y"))}";
        Assert.Equal(("Basic", "sa_x:y"), ModuleCredentialMiddleware.Presented(request, []));
    }

    [Fact]
    public void Bearer_then_a_declared_api_key_header_are_read()
    {
        var bearer = new DefaultHttpContext().Request;
        bearer.Headers.Authorization = "Bearer tok";
        Assert.Equal(("Bearer", "tok"), ModuleCredentialMiddleware.Presented(bearer, ["X-Key"]));

        var keyed = new DefaultHttpContext().Request;
        keyed.Headers["X-Key"] = "k";
        Assert.Equal(("X-Key", "k"), ModuleCredentialMiddleware.Presented(keyed, ["X-Key"]));
        Assert.Equal((null, null), ModuleCredentialMiddleware.Presented(keyed, []));
    }
}
