# Sample files

Test assets for exercising features by hand. Not used by the automated test suite.

## `scanned-invoice-sample.png`

A synthetic "scanned" Swiss invoice (image only — **no text layer**), for testing **OCR search** (ADR 0267). It's deliberately made to look scanned: slight rotation, scan speckle, and a soft blur, while staying readable. The text is German with French/Italian phrases (`eng+deu+fra+ita` OCR).

**How to test:**

1. Bring the stack up to date (the OCR-enabled Tika image + current Api):
   ```bash
   docker compose pull tika          # apache/tika:latest-full (bundles Tesseract)
   docker compose up -d --build api tika
   ```
2. Open http://localhost:8080, log in as the demo admin (`demo@simplarchive.local` / `demo1234`).
3. On the **Repositories** tab, drag `scanned-invoice-sample.png` onto a folder (or use the ribbon **Upload**).
4. Wait ~5–15 s (OCR runs on the async indexer).
5. On the **Search** tab, search for a word that appears **only inside the image**, e.g.:
   - `Xylophonkatze` — a distinctive marker word
   - `Wollishofen`, `Alpsteinwerk`, `Rechnung`, `Klimaanlage`

   The document should appear with a **highlighted snippet** of the OCR'd text. Since none of those words are in the filename, a hit proves the pixels were OCR'd.

The word `Xylophonkatze` is included precisely because it's unique and unambiguous — a search for it can only match via OCR.
