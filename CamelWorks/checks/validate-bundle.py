#!/usr/bin/env python3
"""Check that the bundle manifest and the release workflow agree.

The same three-lists problem the ribbon check exists for, one layer out. A plug-in is named in
PackageContents.xml, built by the release workflow, and copied into a year folder by it — and
nothing connects those. A DLL declared but not copied gives a bundle that loads its ribbon and
fails at the first click; a DLL copied but not declared is dead weight nobody notices.

Also checks the two manifests — the release template in dist/ and the one at the repository root —
declare the same things, since only one of them is the one that ships.
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent

MANIFESTS = [
    ROOT / "PackageContents.xml",
    ROOT / "dist" / "BIMCamel.bundle" / "PackageContents.xml",
]

RELEASE = ROOT / ".github" / "workflows" / "release.yml"
BUILD = ROOT / ".github" / "workflows" / "build.yml"

YEARS = {"2024", "2025", "2026", "2027"}


def fail(message):
    print("FAIL: " + message, file=sys.stderr)
    return 1


def modules(path):
    """{year: {dll, ...}} from a manifest, or None when a ModuleName is malformed."""
    found = {}

    for entry in ET.parse(path).getroot().iter("ComponentEntry"):
        name = entry.get("ModuleName") or ""

        match = re.match(r"^\.\\(\d{4})\\(.+)$", name)
        if not match:
            print("FAIL: ModuleName is not .\\<year>\\<dll>: " + name, file=sys.stderr)
            return None

        found.setdefault(match.group(1), set()).add(match.group(2))

    return found


def main():
    problems = 0
    declared = []

    for path in MANIFESTS:
        found = modules(path)
        if found is None:
            return 1

        if set(found) != YEARS:
            problems |= fail(f"{path.name} covers {sorted(found)}, expected {sorted(YEARS)}")

        if len({frozenset(v) for v in found.values()}) != 1:
            problems |= fail(f"{path.name} declares different modules for different years")

        declared.append((path, next(iter(found.values()))))

    if len({frozenset(m) for _, m in declared}) != 1:
        problems |= fail("the root manifest and the dist template declare different modules: "
                         + " vs ".join(str(sorted(m)) for _, m in declared))

    release = RELEASE.read_text(encoding="utf-8")

    # Everything the manifest promises must be copied by the release workflow AND named in its own
    # validation gate, which is the thing that would actually catch a missing file at release time.
    guarded = set(re.findall(r"'([\w.]+\.dll)'", release))

    for dll in sorted(declared[0][1]):
        if dll not in release:
            problems |= fail(f"{dll} is declared in the manifest but never copied by release.yml")
        elif dll not in guarded:
            problems |= fail(f"{dll} is copied by release.yml but not in its validation gate")

    # And every year the manifest covers must be a year CI actually builds.
    for name, source in (("release.yml", release), ("build.yml", BUILD.read_text(encoding="utf-8"))):
        for year in sorted(YEARS):
            if year not in source:
                problems |= fail(f"year {year} is in the manifest but not in {name}")

    if problems:
        return 1

    print(f"bundle consistent: {len(declared[0][1])} modules x {len(YEARS)} years, "
          "declared, copied and guarded")
    return 0


if __name__ == "__main__":
    sys.exit(main())
