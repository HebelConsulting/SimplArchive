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
async def ocr(file: UploadFile = File(...), lang: str = Query("eng+deu+fra+ita"), kind: str = Query("tiff")):
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
        result = subprocess.run(
            ["ocrmypdf", mode, "--language", lang, "--output-type", "pdf",
             "--image-dpi", "300", src, dst],
            capture_output=True,
        )
        if result.returncode != 0 or not os.path.exists(dst):
            raise HTTPException(status_code=500, detail=result.stderr.decode(errors="replace")[:2000])

        with open(dst, "rb") as handle:
            pdf = handle.read()

    return Response(content=pdf, media_type="application/pdf")
