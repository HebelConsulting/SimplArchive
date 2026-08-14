using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Writer;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// A sample batch scan (issue #492): three short documents separated by two <see cref="PatchCodePage"/>
/// sheets, in one file — what a stack of paper looks like after it has been through a feeder.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the feature can be tried without owning a scanner, and it is a <b>test fixture as much as a
/// demonstration</b>: the same bytes drive the round-trip test, so a detector change that stops recognising
/// the sheet we hand out fails the build rather than being discovered by a user.
/// </para>
/// <para>
/// <b>One page is upside-down and one is crooked, on purpose.</b> Straightening (#491, ADR 0576) runs before
/// this detection and does two different things — <c>--rotate-pages</c> for a page that came out of the feeder
/// 180° round, <c>--deskew</c> for the sub-degree tilt of a sheet that was not square to the glass. A batch of
/// correctly-oriented pages exercises neither, nor the ordering between straightening and detection that is
/// the whole reason the ingest pipeline exists. The crooked page is a full page of text because
/// <b>deskew silently does nothing without a dominant text baseline</b> — a rotated logo or a strip of a few
/// words comes back unchanged with a successful exit, which reads exactly like a broken flag.
/// </para>
/// <para>
/// Generated rather than checked in as a binary: the sheet's geometry lives in one place, so the sample cannot
/// drift away from what the detector is taught to find.
/// </para>
/// </remarks>
public static class PatchCodeSampleBatch
{
    private const double PageWidth = 595;   // A4, points
    private const double PageHeight = 842;
    private const double Margin = 64;

    private const double SkewDegrees = 3.5; // a plausible bad feed: visible, and well inside what deskew fixes

    /// <summary>The batch, as one PDF: document, separator, document (2pp), separator, document.</summary>
    public static byte[] CreatePdf()
    {
        var builder = new PdfDocumentBuilder();
        var body = builder.AddStandard14Font(Standard14Font.Helvetica);
        var heading = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        AddDocumentPage(builder, heading, body, "Invoice 2026-0417", 1, 1, Tilt.None);
        AddSeparator(builder);
        AddDocumentPage(builder, heading, body, "Service agreement", 1, 2, Tilt.None);
        AddDocumentPage(builder, heading, body, "Service agreement", 2, 2, Tilt.UpsideDown);
        AddSeparator(builder);
        AddDocumentPage(builder, heading, body, "Delivery note DN-8842", 1, 1, Tilt.Crooked);

        return builder.Build();
    }

    /// <summary>Which pages of <see cref="CreatePdf"/> are separator sheets, 1-based — what a detector must find.</summary>
    public static IReadOnlyList<int> SeparatorPages { get; } = [2, 5];

    /// <summary>How many pages the batch has, so a test can state the arithmetic rather than discover it.</summary>
    public const int PageCount = 6;

    private enum Tilt
    {
        None,
        UpsideDown,
        Crooked,
    }

    // The separator is the real printable sheet, page-for-page. Producing a lookalike here would let the two
    // drift, and the one that mattered would be the one nobody regenerated.
    private static void AddSeparator(PdfDocumentBuilder builder)
    {
        using var sheet = PdfDocument.Open(PatchCodePage.CreatePdf());
        builder.AddPage(sheet, 1);
    }

    private static void AddDocumentPage(
        PdfDocumentBuilder builder,
        PdfDocumentBuilder.AddedFont heading,
        PdfDocumentBuilder.AddedFont body,
        string title,
        int page,
        int of,
        Tilt tilt)
    {
        var sheet = builder.AddPage(PageWidth, PageHeight);
        sheet.SetTextAndFillColor(0, 0, 0);

        // The transform is applied to the page's whole content, which is what makes this a page that ARRIVED
        // wrong rather than a page drawn to look wrong — the text really is at that angle.
        if (Matrix(tilt) is { } matrix)
        {
            Transform(sheet, matrix);
        }

        var y = PageHeight - Margin;
        sheet.AddText(title, 16m, new PdfPoint(Margin, y), heading);
        y -= 34;

        foreach (var line in Body(title, page, of))
        {
            sheet.AddText(line, 10m, new PdfPoint(Margin, y), body);
            y -= 15;
        }

        sheet.AddText($"Page {page} of {of}", 9m, new PdfPoint(Margin, Margin), body);
    }

    private static decimal[]? Matrix(Tilt tilt) => tilt switch
    {
        // A half turn about the page centre: every point maps to its opposite corner.
        Tilt.UpsideDown => [-1, 0, 0, -1, (decimal)PageWidth, (decimal)PageHeight],
        Tilt.Crooked => Rotation(SkewDegrees),
        _ => null,
    };

    // Rotate about the page centre rather than the origin, so the content stays on the sheet.
    private static decimal[] Rotation(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var (cx, cy) = (PageWidth / 2, PageHeight / 2);

        return
        [
            (decimal)cos,
            (decimal)sin,
            (decimal)(-sin),
            (decimal)cos,
            (decimal)(cx - (cos * cx) + (sin * cy)),
            (decimal)(cy - (sin * cx) - (cos * cy)),
        ];
    }

    /// <summary>
    /// Prepends a <c>cm</c> (concatenate-matrix) operator, so everything drawn afterwards is transformed.
    /// </summary>
    /// <remarks>
    /// PdfPig's page builder exposes no way to set the transformation matrix, and the alternatives are worse
    /// than reaching for the operation list: <c>SetRotation</c> only does right angles (PDF <c>/Rotate</c>), so
    /// it cannot make a page 3.5° crooked, and rasterising a page to rotate the pixels would need a PDF
    /// rasteriser the Api image does not have. The cast is checked rather than assumed, so a PdfPig upgrade
    /// that changes the backing collection fails here with a sentence rather than silently producing a sample
    /// whose pages are all straight — which is the failure that would matter, since a straight sample still
    /// looks entirely correct.
    /// </remarks>
    private static void Transform(PdfPageBuilder page, decimal[] matrix)
    {
        if (page.CurrentStream.Operations is not List<IGraphicsStateOperation> operations)
        {
            throw new InvalidOperationException(
                "PdfPig's content stream no longer exposes a mutable operation list, so the sample batch "
                + "cannot tilt its pages. See PatchCodeSampleBatch.Transform.");
        }

        operations.Add(new ModifyCurrentTransformationMatrix(matrix));
    }

    // Enough lines of real text to give Leptonica a baseline to measure the tilt against, and to put the page
    // well over the detector's "this is a document, not a separator sheet" threshold.
    private static IEnumerable<string> Body(string title, int page, int of)
    {
        yield return $"This page is part of the SimplArchive patch-code sample batch ({title}).";
        yield return "It stands in for a real scanned document so that the separator sheets around it have";
        yield return "something to separate. Nothing here is a real business record.";
        yield return string.Empty;

        for (var paragraph = 1; paragraph <= 4; paragraph++)
        {
            yield return $"{paragraph}. Section {paragraph} of {title}, page {page} of {of}.";

            for (var line = 1; line <= 6; line++)
            {
                yield return "   The quick brown fox jumps over the lazy dog, and the batch is fed into the "
                    + "sheet feeder one page at a time.";
            }

            yield return string.Empty;
        }
    }
}
