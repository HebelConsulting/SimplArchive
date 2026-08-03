using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SimplArchive.EndToEndTests;

// The server-rendered login page carries a language selector and a clean (localized) email placeholder (ADR 0515).
// GET /Account/SetLanguage sets the framework culture cookie the RequestLocalization CookieRequestCultureProvider
// reads, so the login dialog re-renders in the chosen language — and the endpoint refuses a non-local return URL.
[Collection(E2ECollection.Name)]
public class LoginLanguageSelectorTests
{
    private readonly E2EApiFactory _factory;

    public LoginLanguageSelectorTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_page_email_placeholder_is_clean_not_mangled()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        Assert.Contains("placeholder=\"you@example.com\"", html);
        // The old bug rendered `placeholder=" lang="you@ lang="example.com"` — guard against its return.
        Assert.DoesNotContain("lang=\"you@", html);
    }

    [Fact]
    public async Task Login_page_shows_the_language_selector()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        Assert.Contains("class=\"lang-bar\"", html);
        Assert.Contains("/Account/SetLanguage?culture=de", html);
    }

    [Fact]
    public async Task SetLanguage_sets_the_culture_cookie_and_redirects_to_login()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/SetLanguage?culture=de");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Culture"));
    }

    [Fact]
    public async Task SetLanguage_preserves_a_local_return_url()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var returnUrl = "/connect/authorize?client_id=blazor-client";
        var response = await client.GetAsync($"/Account/SetLanguage?culture=de&returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("ReturnUrl=", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SetLanguage_rejects_a_nonlocal_return_url()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/SetLanguage?culture=de&returnUrl=https://evil.example.com");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_page_renders_in_the_chosen_language_when_the_culture_cookie_is_set()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // SetLanguage (auto-followed to the login page) deposits the culture cookie; the next load replays it.
        await client.GetAsync("/Account/SetLanguage?culture=de");
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"de\"", html);
    }
}
