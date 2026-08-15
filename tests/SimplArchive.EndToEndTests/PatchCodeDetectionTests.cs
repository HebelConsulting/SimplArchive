using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using SimplArchive.Api.Inbox;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// The patch-code detector, end to end against the real OCR sidecar (issue #492).
/// </summary>
/// <remarks>
/// <para>
/// <b>The only test that can prove this feature works.</b> The sheet is drawn in C# and read in Python, through
/// a ghostscript rasterisation neither side controls, against tolerances measured in hundredths of an inch —
/// so each half can be perfectly correct on its own terms while the pair does nothing. Unit tests on either
/// side would have passed throughout.
/// </para>
/// <para>
/// It builds the sidecar image itself rather than using the shared fixture, which does not start one: the Api
/// is not involved at all here — the contract under test is HTTP in, page numbers out.
/// </para>
/// </remarks>
public class PatchCodeDetectionTests : IAsyncLifetime
{
    private IContainer? _ocr;
    private HttpClient _http = new();

    public async Task InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(new CommonDirectoryPath(RepoRoot()), "ocr")
            .WithDockerfile("Dockerfile")
            .WithName("simplarchive-ocr-e2e:latest")
            .WithCleanUp(false) // layer-cached across runs; rebuilding tesseract's language packs per run is minutes
            .Build();
        await image.CreateAsync();

        _ocr = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health").ForStatusCode(HttpStatusCode.OK)))
            .Build();

        await _ocr.StartAsync();
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_ocr.Hostname}:{_ocr.GetMappedPublicPort(8080)}"),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_ocr is not null)
        {
            await _ocr.DisposeAsync();
        }
    }

    /// <summary>The sheet we hand out is the sheet we recognise — the round trip the whole feature rests on.</summary>
    [Fact]
    public async Task The_printable_separator_sheet_is_detected_as_a_patch_page()
    {
        var (pageCount, patchPages) = await DetectAsync(PatchCodePage.CreatePdf());

        Assert.Equal(1, pageCount);
        Assert.Equal([1], patchPages);
    }

    /// <summary>
    /// And the sample batch cuts exactly where it says it does — including with an upside-down and a crooked
    /// page beside the separators, which is what makes it worth having as a fixture.
    /// </summary>
    [Fact]
    public async Task The_sample_batch_is_detected_at_its_declared_separator_pages()
    {
        var (pageCount, patchPages) = await DetectAsync(PatchCodeSampleBatch.CreatePdf());

        Assert.Equal(PatchCodeSampleBatch.PageCount, pageCount);
        Assert.Equal(PatchCodeSampleBatch.SeparatorPages, patchPages);
    }

    /// <summary>
    /// A stack of ordinary document pages carries no separators. The direction of this error is chosen: a
    /// missed sheet costs a batch that does not split, while a false one throws a page of somebody's document
    /// away — which is why the detector also refuses a page that is mostly content.
    /// </summary>
    [Fact]
    public async Task A_batch_of_ordinary_pages_has_no_patch_pages()
    {
        var documents = PageComposer.CutAt(
            PatchCodeSampleBatch.CreatePdf(), PageComposer.PageFormat.Pdf, PatchCodeSampleBatch.SeparatorPages);

        var (pageCount, patchPages) = await DetectAsync(PageComposer.Join(documents, PageComposer.PageFormat.Pdf));

        // Derived, not a literal: the batch's shape is stated once on the fixture, and a literal here breaks
        // every time a page is added to it — which is exactly what happened when the blank duplex back arrived.
        Assert.Equal(PatchCodeSampleBatch.PageCount - PatchCodeSampleBatch.SeparatorPages.Count, pageCount);
        Assert.Empty(patchPages);
    }

    // The repo uses a .slnx, which Testcontainers' GetSolutionDirectory does not look for.
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private async Task<(int PageCount, IReadOnlyList<int> PatchPages)> DetectAsync(byte[] pdf)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "in.pdf");

        using var response = await _http.PostAsync("patch-codes?kind=pdf", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            json.GetProperty("pageCount").GetInt32(),
            [.. json.GetProperty("patchPages").EnumerateArray().Select(p => p.GetInt32())]);
    }
}
