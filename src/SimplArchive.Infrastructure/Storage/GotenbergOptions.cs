namespace SimplArchive.Infrastructure.Storage;

// Binds the "Gotenberg" configuration section. Url is the base address of the Gotenberg service (e.g.
// http://gotenberg:3000 in the Compose stack). When empty, office-document preview is unavailable and the
// preview falls back to the original file — see ADR "Office document preview via Gotenberg".
public class GotenbergOptions
{
    public string Url { get; set; } = string.Empty;
}
