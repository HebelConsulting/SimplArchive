"""OCR sidecar: POST a TIFF or a scanned PDF, get a searchable PDF back (ADRs "Searchable PDF successor for
TIFFs" and "Scanned image-only PDF detection").

A thin wrapper over OCRmyPDF. The Api's OcrmypdfConverter posts the original bytes and stores the returned PDF
as a new document version. Kept deliberately minimal — all the real work is OCRmyPDF/Tesseract.

`kind` selects the OCR mode:
  - tiff (default): --force-ocr — the source is a pure page image, so rasterize + OCR into a clean PDF.
  - pdf: --skip-text — the source is a scanned PDF that already carries page images; OCR the pages that lack
    text and PRESERVE the original page images (don't re-rasterize). The Api only sends a PDF here once it has
    detected it as image-only, so in practice every page gets OCR'd.
"""
import os
import subprocess
import tempfile

from fastapi import FastAPI, File, HTTPException, Query, UploadFile
from fastapi.responses import Response

app = FastAPI()


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/ocr")
async def ocr(
    file: UploadFile = File(...),
    lang: str = Query("eng+deu+fra+ita"),
    kind: str = Query("tiff"),
    deskew: bool = Query(False),
):
    data = await file.read()
    is_pdf = kind == "pdf"
    with tempfile.TemporaryDirectory() as work:
        src = os.path.join(work, "in.pdf" if is_pdf else "in.tif")
        dst = os.path.join(work, "out.pdf")
        with open(src, "wb") as handle:
            handle.write(data)

        # tiff → --force-ocr (rasterize the pure image); pdf → --skip-text (OCR image pages, keep the images).
        # --image-dpi 300: fallback resolution when the source carries none.
        mode = "--skip-text" if is_pdf else "--force-ocr"
        args = ["ocrmypdf", mode, "--language", lang, "--output-type", "pdf", "--image-dpi", "300"]

        # Straightening (#491): --deskew is Leptonica's sub-degree correction, --rotate-pages is Tesseract's
        # orientation detection for a page that is 90 or 180 degrees out. They fix different things and a
        # scanner produces both, so they travel together.
        #
        # Free here in a way it is nowhere else: this path already rasterises every page, so straightening adds
        # no conversion that was not happening anyway — and it IMPROVES the OCR in the same pass, since
        # Tesseract reads straight text better. Both flags are incompatible with --redo-ocr, which is why the
        # modes above stay --force-ocr / --skip-text.
        if deskew:
            args += ["--deskew", "--rotate-pages"]

            # --optimize 3 only on this path, and only because this path already re-encodes: straightening
            # cannot happen without rasterising, so the pixels are being rewritten regardless. Measured on a
            # real colour scan: 2.2 MB source -> 10 MB at the default level 1, 2.0 MB at level 3. Level 3 is
            # LOSSY, which is a deliberate trade the caller has made by asking for straightening at all — and
            # it is why a digitally signed document never reaches this code (the pipeline refuses it outright,
            # since any re-encoding voids the signature).
            args += ["--optimize", "3"]

        args += [src, dst]
        result = subprocess.run(args, capture_output=True)
        if result.returncode != 0 or not os.path.exists(dst):
            raise HTTPException(status_code=500, detail=result.stderr.decode(errors="replace")[:2000])

        with open(dst, "rb") as handle:
            pdf = handle.read()

    return Response(content=pdf, media_type="application/pdf")


@app.post("/thumbnail")
async def thumbnail(file: UploadFile = File(...), width: int = Query(600)):
    """POST a PDF, get a PNG of page 1 back, with the page count in `X-Page-Count`.

    Here rather than in the Api because the Api image is Alpine (musl) and the usable PDF rasterisers ship
    glibc-only natives — Docnet/PDFium has no musl build, and the NetVips musl bundle carries no PDF loader. So
    a server-side thumbnail in the Api would have worked in dev and failed inside the container. This image is
    Debian-based and already has ghostscript (ocrmypdf pulls it in), so the capability is here for free.

    Ghostscript is asked for the page count first: -dNODISPLAY with a tiny PostScript program reads it without
    rendering anything, so a 400-page document does not get rasterised twice.
    """
    data = await file.read()
    with tempfile.TemporaryDirectory() as work:
        src = os.path.join(work, "in.pdf")
        dst = os.path.join(work, "page1.png")
        with open(src, "wb") as handle:
            handle.write(data)

        count = subprocess.run(
            ["gs", "-q", "-dNODISPLAY", "-dNOSAFER", "-c",
             f"({src}) (r) file runpdfbegin pdfpagecount = quit"],
            capture_output=True,
        )
        try:
            page_count = int(count.stdout.decode(errors="replace").strip())
        except ValueError:
            # A page count we could not read is not a reason to refuse the picture: the badge is an extra, the
            # thumbnail is the point. 0 means "unknown" to the caller.
            page_count = 0

        # -dLastPage=1: one page only. -r… is derived from the requested width against a 612pt (US Letter/A4-ish)
        # page, which is close enough for a thumbnail and avoids rendering an A0 poster at 600 DPI.
        dpi = max(24, min(300, round(width * 72 / 612)))
        result = subprocess.run(
            ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=png16m",
             "-dFirstPage=1", "-dLastPage=1", f"-r{dpi}", f"-sOutputFile={dst}", src],
            capture_output=True,
        )
        if result.returncode != 0 or not os.path.exists(dst):
            raise HTTPException(status_code=500, detail=result.stderr.decode(errors="replace")[:2000])

        with open(dst, "rb") as handle:
            png = handle.read()

    return Response(content=png, media_type="image/png", headers={"X-Page-Count": str(page_count)})
