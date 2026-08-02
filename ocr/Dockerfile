# OCR sidecar (ADR "Searchable PDF successor for TIFFs"): a thin HTTP wrapper over OCRmyPDF, which drives
# Tesseract to turn a (multi-page) TIFF into a searchable PDF (page image + invisible positioned text layer).
# OCRmyPDF is MPL-2.0, Tesseract Apache-2.0, FastAPI/uvicorn MIT — all used as-is, no bundling into the Api.
FROM python:3.14-slim

# ocrmypdf pulls in tesseract-ocr (eng), ghostscript, qpdf, etc.; add the other supported OCR languages
# (must stay in sync with SimplArchive.Domain.Documents.OcrLanguages). "All versions" = the base pack plus
# Tesseract's variant packs (Middle English enm, old-orthography Spanish spa_old).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ocrmypdf \
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

RUN pip install --no-cache-dir fastapi uvicorn python-multipart

COPY app.py /app/app.py
WORKDIR /app
EXPOSE 8080

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
