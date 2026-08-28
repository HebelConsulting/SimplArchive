using System.Net.Http.Json;
using SimplArchive.Api.Intray;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.EndToEndTests;

// An upload can tell the server it has arrived, so the ingest pipeline runs NOW — deskew, patch-code cutting —
// instead of waiting for IntrayIngestSweepWorker's five-minute fallback poll.
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
[Trait("Area", "e2e-2")]
public class IntrayUploadSignalsIngestTests
{
    private readonly E2EApiFactory _factory;

    public IntrayUploadSignalsIngestTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_upload_response_advertises_processed_and_following_it_cuts_the_batch()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"ingest-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Ingester", canManageRepositories: true);
        var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var name = $"batch-{Guid.NewGuid():N}.pdf";

        var upload = await TestJson.Post(client, "/api/intray", new { fileName = name });

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

        // Following it runs the pipeline synchronously and answers with what is in the intray afterwards.
        //
        // Deliberately NOT asserting three parts here: this factory leaves `Ocr` unset, so the API resolves
        // NullPatchCodeDetector and no separator can be found — the cut is exercised where a sidecar exists
        // (PatchCodeDetectionTests drives the real container over HTTP). What THIS test owns is the link and
        // the round trip: that a client can reach ingest at all, which is what was broken.
        var written = (await TestJson.Post(client, processed!, new { }))
            .GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();

        Assert.NotEmpty(written);

        // And the intray really holds the result, without a sweep having run.
        var listed = (await TestJson.Get(client, "/api/intray")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!).ToList();

        Assert.All(written, w => Assert.Contains(w, listed));
    }

    // Deleting an item must take its exactly-once ingest marker with it: the marker left behind made a
    // RE-UPLOAD under the same name skip the whole pipeline (no straighten, no cut) in a silent no-op —
    // found live when a corrected test batch "failed completely" (review, 2026-08-16).
    [Fact]
    public async Task Deleting_an_item_clears_its_ingest_marker_so_a_same_name_reupload_is_processed_again()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"ingest-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Ingester", canManageRepositories: true);
        var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var name = $"again-{Guid.NewGuid():N}.pdf";
        using var storage = new HttpClient();

        async Task<List<string>> UploadAndProcessAsync()
        {
            var upload = await TestJson.Post(client, "/api/intray", new { fileName = name });
            (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(PatchCodeSampleBatch.CreatePdf()))).EnsureSuccessStatusCode();
            var processed = upload.GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "processed").GetProperty("href").GetString()!;
            return (await TestJson.Post(client, processed, new { }))
                .GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();
        }

        var first = await UploadAndProcessAsync();
        Assert.NotEmpty(first);

        // Delete every item the first round produced (the pipeline may have renamed/cut), then re-upload
        // under the SAME name. An empty `names` on the second round is the stale-marker bug.
        foreach (var item in (await TestJson.Get(client, "/api/intray")).GetProperty("items").EnumerateArray()
                     .Select(i => i.GetProperty("name").GetString()!).Where(n => n.StartsWith(name[..8])).ToList())
        {
            (await client.DeleteAsync($"/api/intray/{Uri.EscapeDataString(item)}")).EnsureSuccessStatusCode();
        }

        Assert.NotEmpty(await UploadAndProcessAsync());
    }
}
