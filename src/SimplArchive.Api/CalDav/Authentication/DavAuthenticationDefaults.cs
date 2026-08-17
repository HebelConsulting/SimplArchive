// PORTED from the sister project SimplCalCon (Apache-2.0, same licence) — see ADR 0621.
namespace SimplArchive.Api.CalDav.Authentication;

public static class DavAuthenticationDefaults
{
    /// <summary>Authentication scheme name for DAV HTTP Basic app-password auth.</summary>
    public const string Scheme = "Dav";

    public const string Realm = "SimplArchive DAV";
}
