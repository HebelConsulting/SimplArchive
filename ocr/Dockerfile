# OCR sidecar (ADR "Searchable PDF successor for TIFFs"): a thin HTTP wrapper over OCRmyPDF, which drives
# Tesseract to turn a (multi-page) TIFF into a searchable PDF (page image + invisible positioned text layer).
# OCRmyPDF is MPL-2.0, Tesseract Apache-2.0, FastAPI/uvicorn MIT — all used as-is, no bundling into the Api.
FROM python:3.14-slim

# ocrmypdf pulls in tesseract-ocr (eng), ghostscript, qpdf, etc.; add the other supported OCR languages
# (must stay in sync with SimplArchive.Domain.Documents.OcrLanguages). "All versions" = the base pack plus
# Tesseract's variant packs (Middle English enm, old-orthography Spanish spa_old).
# pngquant unlocks OCRmyPDF's --optimize 2/3, which is not a nicety: straightening a COLOUR scan rasterises
# and re-encodes it, and at the default --optimize 1 a 2.2 MB scan came back 10 MB — a 4.4x inflation on a
# feature that is on by default, in a product with storage quotas. With pngquant, --optimize 3 returns 2.0 MB,
# i.e. slightly SMALLER than the source, for about four more seconds. Without it, levels 2 and 3 simply fail.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ocrmypdf \
        pngquant \
        qpdf \
        tesseract-ocr-deu \
        tesseract-ocr-fra \
        tesseract-ocr-ita \
        tesseract-ocr-enm \
        tesseract-ocr-spa \
        tesseract-ocr-spa-old \
        tesseract-ocr-por \
        tesseract-ocr-ron \
        tesseract-ocr-rus \
    && rm -rf /var/lib/apt/lists/*

# numpy (BSD-3-Clause) and Pillow (MIT-CMU) are patch-code detection (#492): Pillow decodes the rasterised
# pages and multi-page TIFF frames, numpy does the projection profile. A per-pixel loop in plain Python over a
# 1240x1754 page is not a micro-optimisation away from being usable — it is seconds per page.
RUN pip install --no-cache-dir fastapi uvicorn python-multipart numpy pillow

COPY app.py /app/app.py
COPY patchcode.py /app/patchcode.py
WORKDIR /app
EXPOSE 8080

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
