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
import re
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

# Below this Tesseract-OSD confidence an orientation verdict is ignored. OCRmyPDF's own default (14) sits
# ABOVE what OSD reports for a genuinely upside-down page of a real 1-bit office scan (~11.6 measured on the
# shipped sample batch), which silently disabled rotation for every upload. Upright pages report angle 0 and
# are never touched, and a failed OSD (blank/sparse page) is angle 0 confidence 0 — so this only guards
# against a CONFIDENT-but-wrong non-zero verdict, and 2 is enough.
ROTATE_CONFIDENCE = 2.0


def _rotate_pdf_pages(src: str, work: str) -> None:
    """Straighten 90/180/270-degree pages of a PDF in place, losslessly (issue: review 2026-08-16).

    OCRmyPDF's --rotate-pages cannot do this here: in --skip-text mode it skips ALL processing — the
    orientation pass included — on any page that already carries a text layer, and the PDFs this endpoint
    receives are scanned batches whose content pages usually do (a prior OCR, a mixed batch). So the sidecar
    asks Tesseract's OSD per page itself — on a Ghostscript raster, which applies the existing /Rotate, so a
    correction composes — and applies the turns with qpdf, which only rewrites the /Rotate attribute.
    """
    npages = subprocess.run(["qpdf", "--show-npages", src], capture_output=True)
    if npages.returncode != 0:
        return

    corrections = []
    for page in range(1, int(npages.stdout.strip() or 0) + 1):
        png = os.path.join(work, f"osd_{page}.png")
        raster = subprocess.run(
            ["gs", "-dQUIET", "-dSAFER", "-dBATCH", "-dNOPAUSE", "-sDEVICE=pnggray",
             f"-dFirstPage={page}", f"-dLastPage={page}", "-r150", "-o", png, "-f", src],
            capture_output=True)
        if raster.returncode != 0:
            continue

        osd = subprocess.run(["tesseract", "-l", "osd", "--psm", "0", png, "stdout"], capture_output=True).stdout
        angle = re.search(rb"Orientation in degrees: (\d+)", osd)
        confidence = re.search(rb"Orientation confidence: ([\d.]+)", osd)
        if angle and confidence and int(angle.group(1)) % 360 != 0 and float(confidence.group(1)) >= ROTATE_CONFIDENCE:
            corrections.append((page, int(angle.group(1)) % 360))

    if corrections:
        dst = os.path.join(work, "rotated.pdf")
        # --warning-exit-0: qpdf exits 3 for recoverable input warnings (real scans trip these), which
        # would otherwise read as failure and silently discard the correction.
        args = ["qpdf", "--warning-exit-0", src, dst] + [f"--rotate=+{angle}:{page}" for page, angle in corrections]
        if subprocess.run(args, capture_output=True).returncode == 0 and os.path.exists(dst):
            os.replace(dst, src)


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
    force: bool = Query(False),
):
    data = await file.read()
    is_pdf = kind == "pdf"
    with tempfile.TemporaryDirectory() as work:
        src = os.path.join(work, "in.pdf" if is_pdf else "in.tif")
        dst = os.path.join(work, "out.pdf")
        with open(src, "wb") as handle:
            handle.write(data)

        # tiff → --force-ocr (rasterize the pure image); pdf → --skip-text (OCR image pages, keep the images).
        # EXCEPT a pdf with deskew requested: the caller only asks for that on a PDF it detected as a SCAN,
        # and --skip-text would skip the correction on every page that already carries a text layer — the
        # same skip that broke rotation — so the scan is re-rendered like a TIFF.
        # --image-dpi 300: fallback resolution when the source carries none.
        # force (#999's Make searchable): the user overruled the detector, so re-render like a TIFF —
        # --skip-text would skip exactly the pages they want redone (a bad text layer), and a
        # detector-blind scan converts under either mode.
        mode = "--skip-text" if is_pdf and not deskew and not force else "--force-ocr"
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
        if rotate and is_pdf and not deskew:
            # NOT --rotate-pages: in --skip-text mode it skips the orientation pass on any page that
            # already carries text (see _rotate_pdf_pages), which is most pages of most PDFs sent here.
            # (With deskew the mode is --force-ocr, where OCRmyPDF's own pass works — the branch below.)
            _rotate_pdf_pages(src, work)
        elif rotate:
            # TIFF goes through --force-ocr (a full re-render), where OCRmyPDF's own pass works — but its
            # default confidence threshold does not (see ROTATE_CONFIDENCE).
            args += ["--rotate-pages", "--rotate-pages-threshold", str(ROTATE_CONFIDENCE)]

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
