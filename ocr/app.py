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
import glob
import os
import subprocess
import tempfile

import numpy as np
from fastapi import FastAPI, File, HTTPException, Query, UploadFile
from fastapi.responses import Response
from PIL import Image, ImageSequence

import patchcode

app = FastAPI()

# What patch-code detection rasterises at. Well over the ~40 px a 0.08 in narrow bar needs to be measurable,
# and far under what a 600 dpi colour scan would cost to hold in memory a page at a time.
PATCH_DPI = 150


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/ocr")
async def ocr(
    file: UploadFile = File(...),
    lang: str = Query("eng+deu+fra+ita"),
    kind: str = Query("tiff"),
    deskew: bool = Query(False),
    rotate: bool = Query(False),
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

        # Straightening (#491) is TWO corrections, and they are asked for separately because they cost
        # differently (#492 follow-up):
        #
        #   --rotate-pages  Tesseract's orientation detection for a page 90 or 180 degrees out. On a PDF this
        #                   only sets the page's /Rotate attribute, so it is LOSSLESS — no rasterising, no
        #                   re-encoding, the original text survives. That is why it may run on PDFs at all.
        #   --deskew        Leptonica's sub-degree correction. It CANNOT be applied without re-rendering the
        #                   page, which is why the caller only ever asks for it on a TIFF: doing it to a
        #                   digital-born PDF would trade real text for an OCR approximation.
        #
        # They used to travel together behind one flag, and the TIFF-only gate that deskew needs was silently
        # inherited by rotation, which needs no such thing.
        if rotate:
            args += ["--rotate-pages"]

        if deskew:
            args += ["--deskew"]

            # --optimize 3 belongs to DESKEW alone, and only because deskew already re-encodes: it cannot
            # happen without rasterising, so the pixels are being rewritten regardless. Measured on a real
            # colour scan: 2.2 MB source -> 10 MB at the default level 1, 2.0 MB at level 3. Level 3 is LOSSY,
            # a deliberate trade the caller made by asking for deskew — and it is why a digitally signed
            # document never reaches this code (the pipeline refuses it outright, since any re-encoding voids
            # the signature). Rotation must NOT pull this in: it would turn a lossless operation into a lossy
            # one for no reason.
            args += ["--optimize", "3"]

        args += [src, dst]
        result = subprocess.run(args, capture_output=True)
        if result.returncode != 0 or not os.path.exists(dst):
            raise HTTPException(status_code=500, detail=result.stderr.decode(errors="replace")[:2000])

        with open(dst, "rb") as handle:
            pdf = handle.read()

    return Response(content=pdf, media_type="application/pdf")


@app.post("/patch-codes")
async def patch_codes(file: UploadFile = File(...), kind: str = Query("pdf")):
    """POST a PDF or a multi-page TIFF, get back which of its pages are Patch 3 separator sheets (issue #492).

    Detection only — the caller does the cutting, because the page algebra it already owns produces the same
    result for both formats and this endpoint would otherwise have to reproduce it in Python.

    Returns `{"pageCount": n, "patchPages": [2, 5]}` with 1-based page numbers. An empty list is the ordinary
    answer for a batch nobody put separators in, and is not an error.
    """
    data = await file.read()
    with tempfile.TemporaryDirectory() as work:
        pages = _tiff_pages(data, work) if kind == "tiff" else _pdf_pages(data, work)

        found = []
        count = 0
        for count, (gray, dpi) in enumerate(pages, start=1):
            if patchcode.find_patch3(gray, dpi):
                found.append(count)

    return {"pageCount": count, "patchPages": found}


def _pdf_pages(data: bytes, work: str):
    """Every page as an 8-bit greyscale array, rasterised by ghostscript at a known DPI."""
    src = os.path.join(work, "in.pdf")
    with open(src, "wb") as handle:
        handle.write(data)

    result = subprocess.run(
        ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pnggray",
         f"-r{PATCH_DPI}", f"-sOutputFile={os.path.join(work, 'p-%05d.png')}", src],
        capture_output=True,
    )
    rendered = sorted(glob.glob(os.path.join(work, "p-*.png")))
    if result.returncode != 0 and not rendered:
        raise HTTPException(status_code=500, detail=result.stderr.decode(errors="replace")[:2000])

    for path in rendered:
        with Image.open(path) as page:
            yield np.asarray(page.convert("L")), float(PATCH_DPI)


def _tiff_pages(data: bytes, work: str):
    """Every frame of a multi-page TIFF, downscaled to the detection DPI so a 600 dpi scan is not carried whole."""
    src = os.path.join(work, "in.tif")
    with open(src, "wb") as handle:
        handle.write(data)

    with Image.open(src) as tiff:
        for frame in ImageSequence.Iterator(tiff):
            gray = frame.convert("L")

            # Scanner TIFFs carry XResolution in practice (the Api's own scan sniff leans on it). Without one,
            # assume the long side is a sheet of paper: A4 is 11.7 in and Letter 11.0, so 11.5 is under 5 %
            # out either way — inside every tolerance the detector uses, all of which are ratios or generous.
            dpi = float((gray.info.get("dpi") or (0, 0))[0]) or max(gray.size) / 11.5
            if dpi > PATCH_DPI * 1.5:
                scale = PATCH_DPI / dpi
                gray = gray.resize((max(1, int(gray.width * scale)), max(1, int(gray.height * scale))))
                dpi = PATCH_DPI

            yield np.asarray(gray), dpi


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
