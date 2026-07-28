using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// Small JSON helpers for the end-to-end tests — surface the API's Problem-Details body on a non-2xx so a
// failing assertion shows the errorCode/detail instead of a bare status code.
internal static class TestJson
{
    public static async Task<JsonElement> Post(HttpClient client, string url, object body) =>
        await Read(await client.PostAsJsonAsync(url, body));

    public static async Task<JsonElement> Put(HttpClient client, string url, object body) =>
        await Read(await client.PutAsJsonAsync(url, body));

    public static async Task<JsonElement> Get(HttpClient client, string url) =>
        await Read(await client.GetAsync(url));

    public static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
