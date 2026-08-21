#!/usr/bin/env bash
# Validate written BCF against the published buildingSMART schemas.
#
# Run after the test suite: BcfWriterTests dumps real writer output into a
# "bcf-validation" directory under the test binaries, and this checks every file in
# it. Takes that directory as its argument, or finds it.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-}"

if [[ -z "$out" ]]; then
  out="$(find "$here/../.." -type d -name bcf-validation -print -quit || true)"
fi

if [[ -z "$out" || ! -d "$out" ]]; then
  echo "no bcf-validation directory found; run the tests first" >&2
  exit 1
fi

fail=0
checked=0

check() {
  local schema="$1" file="$2"
  if xmllint --noout --schema "$schema" "$file"; then
    checked=$((checked + 1))
  else
    fail=1
  fi
}

shopt -s nullglob
for file in "$out"/v21/*.markup.xml;   do check "$here/markup21.xsd"  "$file"; done
for file in "$out"/v21/*.bcfv.xml;     do check "$here/visinfo21.xsd" "$file"; done
for file in "$out"/v21/*.version.xml;  do check "$here/version21.xsd" "$file"; done
for file in "$out"/v30/*.markup.xml;   do check "$here/markup30.xsd"  "$file"; done
for file in "$out"/v30/*.bcfv.xml;     do check "$here/visinfo30.xsd" "$file"; done
for file in "$out"/v30/*.version.xml;  do check "$here/version30.xsd" "$file"; done

if [[ "$checked" -eq 0 ]]; then
  echo "no BCF files were checked, which means the dump did not run" >&2
  exit 1
fi

echo "$checked BCF files validated against the published schemas"
exit "$fail"
