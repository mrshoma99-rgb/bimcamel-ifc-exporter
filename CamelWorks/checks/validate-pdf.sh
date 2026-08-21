#!/usr/bin/env bash
# Structurally check the PDFs the writer produces.
#
# Unit tests can prove the writer emitted the bytes it meant to. They cannot prove a reader will
# accept the result: the cross-reference table, the object graph and the stream lengths all have to
# agree, and an off-by-one there yields a file that opens in one viewer and fails in another.
# qpdf walks all of it.
#
# PdfReportWriterTests dumps real output into a "pdf-validation" directory under the test
# binaries. Takes that directory as its argument, or finds it.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-}"

if [[ -z "$out" ]]; then
  out="$(find "$here/.." -type d -name pdf-validation -print -quit || true)"
fi

if [[ -z "$out" || ! -d "$out" ]]; then
  echo "no pdf-validation directory found; run the tests first" >&2
  exit 1
fi

shopt -s nullglob
files=("$out"/*.pdf)

if [[ "${#files[@]}" -eq 0 ]]; then
  echo "no PDFs were checked, which means the dump did not run" >&2
  exit 1
fi

fail=0
for file in "${files[@]}"; do
  echo "--- $(basename "$file")"
  qpdf --check "$file" || fail=1
done

if [[ "$fail" -ne 0 ]]; then
  echo "at least one PDF did not pass qpdf --check" >&2
  exit 1
fi

echo "${#files[@]} PDFs pass qpdf --check"
