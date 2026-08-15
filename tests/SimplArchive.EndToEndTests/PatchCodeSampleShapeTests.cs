using SimplArchive.Api.Inbox;
using SimplArchive.Infrastructure.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SimplArchive.EndToEndTests;

// The sample batch really is what it says it is (#492). Pure PdfPig assertions — no fixture, no containers;
// they live in this project only because the sample is composed from the Api's embedded demo documents.
//
// Worth having as tests rather than trusting the composer: every property below is invisible in a rendering.
// A sample whose pages are all upright, or which groups the wrong pages, looks entirely correct on screen and
// silently stops exercising the thing it exists to exercise.
public class PatchCodeSampleShapeTests
{
    [Fact]
    public void The_batch_has_the_pages_and_separators_it_declares()
    {
        using var batch = PdfDocument.Open(PatchCodeSampleBatch.CreatePdf());

        Assert.Equal(PatchCodeSampleBatch.PageCount, batch.NumberOfPages);
    }

    /// <summary>
    /// One page really does arrive upside-down — the reason this is a fixture and not a picture. Auto-rotate
    /// runs before the cut and corrects exactly this, and a sample of upright pages would exercise nothing.
    /// </summary>
    [Fact]
    public void The_declared_page_really_is_upside_down_and_the_rest_are_not()
    {
        using var batch = PdfDocument.Open(PatchCodeSampleBatch.CreatePdf());

        // The page's own /Rotate, not its letters' orientation: the content is a copied document that is
        // internally upright, and the 180 is how the PDF says to DISPLAY it upside-down — which is what a
        // rasteriser, and therefore the correction, actually sees.
        Assert.Equal(180, batch.GetPage(PatchCodeSampleBatch.UpsideDownPage).Rotation.Value);

        // And every other page is upright, so the assertion above is about THIS page rather than about a
        // rotation accidentally applied to all of them.
        for (var page = 1; page <= batch.NumberOfPages; page++)
        {
            if (page != PatchCodeSampleBatch.UpsideDownPage)
            {
                Assert.Equal(0, batch.GetPage(page).Rotation.Value);
            }
        }
    }

    /// <summary>
    /// The blank page really is blank — it is the back of a duplex-scanned sheet, and the page the user is
    /// meant to drop with Sort pages. A fixture whose "blank" page carries content would demonstrate nothing.
    /// </summary>
    [Fact]
    public void The_duplex_back_is_blank()
    {
        using var batch = PdfDocument.Open(PatchCodeSampleBatch.CreatePdf());

        Assert.Empty(batch.GetPage(PatchCodeSampleBatch.BlankPage).Letters);

        // And the pages around it are not, so "blank" is a property of THAT page rather than of the reader.
        Assert.NotEmpty(batch.GetPage(PatchCodeSampleBatch.UpsideDownPage).Letters);
    }

    /// <summary>
    /// Nothing is crooked, deliberately: deskew declines PDFs because it cannot run without re-rendering, so a
    /// crooked page here would demonstrate a correction that can never be applied to this file.
    /// </summary>
    [Fact]
    public void No_page_is_crooked_because_deskew_cannot_act_on_a_pdf()
    {
        using var batch = PdfDocument.Open(PatchCodeSampleBatch.CreatePdf());

        for (var page = 1; page <= batch.NumberOfPages; page++)
        {
            Assert.All(
                batch.GetPage(page).Letters,
                l => Assert.NotEqual(TextOrientation.Other, l.TextOrientation));
        }
    }

    /// <summary>Cutting at the separators gives back the documents that went into it.</summary>
    [Fact]
    public void Cutting_at_the_separator_pages_yields_the_documents_between_them()
    {
        var parts = PageComposer.CutAt(
            PatchCodeSampleBatch.CreatePdf(), PageComposer.PageFormat.Pdf, PatchCodeSampleBatch.SeparatorPages);

        Assert.Equal(3, parts.Count);

        // 1, 3, 1 — and the THREE is what makes this a real test: with three single-page documents, "cut at the
        // separators" and "split every page" give the same answer. The middle document is the agreement's two
        // pages plus the blank duplex back that came with them.
        Assert.Equal([1, 3, 1], parts.Select(p => PageComposer.CountPages(p, PageComposer.PageFormat.Pdf)));
    }

    /// <summary>
    /// The browsable copies under <c>/download/samples/</c> still match what the code composes.
    /// </summary>
    /// <remarks>
    /// They are checked in rather than written at startup, because the container's <c>wwwroot</c> belongs to
    /// root while the app runs as a non-root user — a runtime write is refused, and loosening that to publish
    /// three sample files would be the wrong trade. So they can go stale, and this is what notices.
    /// <para>
    /// By SHAPE, not bytes: PdfPig stamps a fresh document id into every build, so two runs of the same
    /// composer never produce identical files. Shape is what actually matters anyway — a copy with the wrong
    /// page count or a missing upside-down page teaches the wrong thing, while one differing only in its id
    /// teaches nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_browsable_copy_matches_what_the_composer_produces()
    {
        var checkedIn = Path.Combine(RepoRoot(), "src", "SimplArchive.Api", "wwwroot", "download", "samples",
            "SimplArchive-Patch3-Sample-Batch.pdf");

        Assert.True(File.Exists(checkedIn),
            $"{checkedIn} is missing — run scripts/generate-scan-sample.sh.");

        using var published = PdfDocument.Open(File.ReadAllBytes(checkedIn));
        using var composed = PdfDocument.Open(PatchCodeSampleBatch.CreatePdf());

        Assert.Equal(composed.NumberOfPages, published.NumberOfPages);

        for (var page = 1; page <= composed.NumberOfPages; page++)
        {
            Assert.Equal(composed.GetPage(page).Rotation.Value, published.GetPage(page).Rotation.Value);
            Assert.Equal(composed.GetPage(page).Letters.Count, published.GetPage(page).Letters.Count);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
