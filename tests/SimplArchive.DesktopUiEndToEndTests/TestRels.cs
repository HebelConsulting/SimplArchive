using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// Rel-resolution helpers for tests that hold a bare document id (usually straight from UploadFileAsync):
/// the conforming way back to the resource is the LISTING row's advertised `self` (ADR 0555), never a
/// composed path — tests follow the same rules the clients do, or they prove nothing about them (#443).
/// </summary>
internal static class TestRels
{
    public static async Task<string> DocumentSelfAsync(SimplArchiveApiClient api, SimplArchiveApiClient.Node repo, Guid documentId) =>
        (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Id == documentId).Href("self");
}
