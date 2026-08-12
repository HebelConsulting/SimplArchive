using System.Net.Http.Json;

namespace SimplArchive.Client.Services;

/// <summary>Starting an upload into the caller's inbox: one presigned PUT per file.</summary>
/// <remarks>
/// Extracted at the SECOND caller, which is where CLAUDE.md says to stop copying. The Inbox tab has staged
/// uploads this way since the tab existed; dropping files onto the Personal ▸ Inbox tree node (#467) is the
/// second, and it lives in a different component with its own <c>DotNetObjectReference</c> — so without this the
/// same POST would have been written twice, in two files, with two error-handling stories.
/// </remarks>
public sealed class InboxUploads(HttpClient http, ApiRoot apiRoot)
{
    /// <summary>
    /// A presigned PUT for one file, or <c>null</c> when the inbox refused it. Null rather than an exception
    /// because the caller is a JS-invoked upload loop that reports per-file failures and continues with the
    /// rest — one bad file must not abandon the others.
    /// </summary>
    public async Task<string?> CreateTargetAsync(string fileName)
    {
        try
        {
            var response = await http.PostAsJsonAsync(await apiRoot.RequireAsync("inbox"), new { fileName });
            return (await response.Content.ReadFromJsonAsync<UploadInboxResponse>())?.UploadUrl;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public sealed record UploadInboxResponse
    {
        public string UploadUrl { get; set; } = "";
    }
}
