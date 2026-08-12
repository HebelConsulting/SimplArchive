namespace SimplArchive.Client.Services;

/// <summary>
/// PUT a cropped profile photo to an already-resolved address.
/// </summary>
/// <remarks>
/// Two dialogs now upload a photo — the standalone <c>ProfilePhotoDialog</c> (the admin path) and the
/// "Edit profile" dialog that hosts the same editor inline (#464) — and the request has a detail that is wrong
/// by default: the body must carry <c>image/png</c>, or the server rejects bytes it would otherwise accept.
/// One implementation rather than two copies, per the repo's rule about the same work across several call sites.
///
/// It takes the address; it does not resolve it. Where the photo lives is the caller's business (their own
/// <c>photo</c> rel, or another user's followed from that user's row), and this stays free of that decision.
/// </remarks>
public static class ProfilePhotoUpload
{
    public static Task<HttpResponseMessage> PutAsync(HttpClient http, string photoHref, string base64Png)
    {
        var content = new ByteArrayContent(Convert.FromBase64String(base64Png));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return http.PutAsync(photoHref, content);
    }
}
