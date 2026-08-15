using System.Net.Http.Json;
using SimplArchive.Api.Inbox;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.EndToEndTests;

// An upload can tell the server it has arrived, so the ingest pipeline runs NOW — deskew, patch-code cutting —
// instead of waiting for InboxIngestSweepWorker's five-minute fallback poll.
//
// This is the guard the feature never had. The `processed` endpoint existed, worked, and was covered by nothing
// that asserted a CLIENT could reach it: the upload response advertised no rel to it, so both clients skipped
// the step entirely and every upload waited for the sweep. The visible symptoms were "the split documents do
// not show up after uploading" and "the crooked page was not straightened" — one missing link, two bug reports,
// and a green suite throughout (ADR 0543: an action no resource links to is unreachable, and incomplete).
//
// So the assertion is deliberately on the REL, not on the endpoint: calling a path this test composed itself
// would pass just as happily with the link missing, which is exactly the state that shipped.
[Collection(E2ECollection.Name)]
public class InboxUploadSignalsIngestTests
{
    private readonly E2EApiFactory _factory;

    public InboxUploadSignalsIngestTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_upload_response_advertises_processed_and_following_it_cuts_the_batch()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"ingest-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Ingester", canManageRepositories: true);
        var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var name = $"batch-{Guid.NewGuid():N}.pdf";

        var upload = await TestJson.Post(client, "/api/inbox", new { fileName = name });

        // The rel the clients follow. Its absence is the whole bug.
        var processed = upload.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "processed")
            .GetProperty("href").GetString();

        Assert.False(string.IsNullOrEmpty(processed),
            "The upload response must advertise a `processed` rel, or no conforming client can trigger ingest "
            + "and every upload silently waits for the sweep worker.");

        using var storage = new HttpClient();
        (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!,
            new ByteArrayContent(PatchCodeSampleBatch.CreatePdf()))).EnsureSuccessStatusCode();

        // Following it runs the pipeline synchronously and answers with what is in the inbox afterwards.
        //
        // Deliberately NOT asserting three parts here: this factory leaves `Ocr` unset, so the API resolves
        // NullPatchCodeDetector and no separator can be found — the cut is exercised where a sidecar exists
        // (PatchCodeDetectionTests drives the real container over HTTP). What THIS test owns is the link and
        // the round trip: that a client can reach ingest at all, which is what was broken.
        var written = (await TestJson.Post(client, processed!, new { }))
            .GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();

        Assert.NotEmpty(written);

        // And the inbox really holds the result, without a sweep having run.
        var listed = (await TestJson.Get(client, "/api/inbox")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).ToList();

        Assert.All(written, w => Assert.Contains(w, listed));
    }
}
