#!/usr/bin/env bash
#
# generate-scan-sample.sh — rebuild the patch-code sample batch as a SCAN (issue #492).
#
# The PDF sample is composed at request time from embedded documents (Api/Intray/PatchCodeSampleBatch). The TIFF
# cannot be: building it means rasterising PDFs, and the Api image has no PDF rasteriser — that is what the OCR
# sidecar exists for. So the TIFF is generated here, once, and checked in beside the other demo documents.
#
# It must stay page-for-page identical to the PDF sample, or the two teach different things:
#
#   1  invoice                     4  agreement page 2, UPSIDE-DOWN
#   2  separator sheet             5  blank duplex back
#   3  agreement page 1            6  separator sheet
#                                  7  invoice, CROOKED by 3.5 degrees
#
# Bilevel G4, because that is what a document scanner emits — and because it is 78 KB against 906 KB for the
# same pages in grayscale.
#
# Requires: poppler (pdftoppm), ImageMagick (magick), and a running API for the separator sheet.
# Verify afterwards by feeding the result to the OCR sidecar's /patch-codes: expect [2, 6].
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
demo="$repo_root/src/SimplArchive.Api/DemoData"
api="${API_URL:-http://localhost:8080}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

command -v pdftoppm >/dev/null || { echo "pdftoppm (poppler) is required." >&2; exit 2; }
command -v magick   >/dev/null || { echo "ImageMagick (magick) is required." >&2; exit 2; }

# The separator sheet comes from the running API rather than being drawn here: it is the sheet the detector is
# taught to find, and a lookalike would let the two drift.
curl -fsS -o "$work/sep.pdf" "$api/api/intray/patch-code-sheet" \
  || { echo "Could not fetch the separator sheet from $api — is the API running?" >&2; exit 2; }

page() { pdftoppm -r 200 -gray -png -f "$3" -l "$3" -singlefile "$1" "$work/$2"; }

page "$demo/sample-invoice.pdf"          p1 1
page "$work/sep.pdf"                     p2 1
page "$demo/maintenance-agreement.pdf"   p3 1
page "$demo/maintenance-agreement.pdf"   p4raw 2
page "$demo/choc-invoice-v1.pdf"         p7raw 1
cp "$work/p2.png" "$work/p6.png"

magick "$work/p4raw.png" -rotate 180 +repage "$work/p4.png"
magick -size 1654x2339 xc:white "$work/p5.png"          # the blank duplex back
magick "$work/p7raw.png" -background white -rotate 3.5 +repage "$work/p7.png"

magick "$work/p1.png" "$work/p2.png" "$work/p3.png" "$work/p4.png" \
       "$work/p5.png" "$work/p6.png" "$work/p7.png" \
       -threshold 60% -type bilevel -compress Group4 \
       "$demo/patch-code-sample-batch.tif"

printf 'wrote %s (%s bytes, %s pages)\n' \
  "$demo/patch-code-sample-batch.tif" \
  "$(wc -c < "$demo/patch-code-sample-batch.tif" | tr -d ' ')" \
  "$(magick identify "$demo/patch-code-sample-batch.tif" | wc -l | tr -d ' ')"

# The browsable copies under /download/samples/ (#492). Checked in rather than written at startup: the
# container's wwwroot belongs to root and the app runs as a non-root user, so a runtime write is refused — and
# rather than loosen that, the files are produced here. They cannot be byte-compared in a test, because PdfPig
# stamps a fresh document id into every build; PatchCodeSampleShapeTests guards their SHAPE instead, so a
# composition change fails the build until this script is re-run.
samples="$repo_root/src/SimplArchive.Api/wwwroot/download/samples"
mkdir -p "$samples"
curl -fsS -o "$samples/SimplArchive-Patch3-Separator.pdf"    "$api/api/intray/patch-code-sheet"
curl -fsS -o "$samples/SimplArchive-Patch3-Sample-Batch.pdf" "$api/api/intray/patch-code-sample"
cp "$demo/patch-code-sample-batch.tif" "$samples/SimplArchive-Patch3-Sample-Scan.tif"

printf 'wrote %s browsable copies to %s\n' 3 "$samples"
