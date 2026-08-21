# CamelWorks — what is actually built

`CAMELWORKS_IMPLEMENTATION.md` is the spec. This file is the state: what exists, how each piece is
verified, and what is not wired yet. It is deliberately separate, because a spec that quietly edits
itself to match the code stops being able to tell you the code is wrong.

Every claim below is checked by CI on every push. Where something is unverifiable in CI, it says so.

---

## How each layer is verified

| Layer | Verified by | Where |
|---|---|---|
| `CamelWorks.Core` | Compiler + unit tests, on Linux | `core` job |
| BCF output | `xmllint` against the published buildingSMART XSDs | `core` job |
| PDF output | `qpdf --check` over real writer output | `core` job |
| Ribbon consistency | A script comparing the markup, the attributes and the catalogue | `core` job |
| `CamelWorks.Nav`, `.Nav.Clash`, `.UI` | Compiler, once per Navisworks year | 4 × Windows jobs |
| Anything visual | **Not verified.** No Navisworks in CI | — |

The last row is the honest limit. Every host adapter and every screen compiles against all four
supported releases; none of it has been run inside Navisworks from this environment.

---

## Core services — built and tested

**Identity.** `ElementKey` on three rungs (instance GUID, tree path, geometry signature) and
`ClashKey` whose occurrence discriminator is a quantised position rather than an ordinal — an
ordinal renumbers the moment one of three penetrations is fixed, which is the run that matters.
Boundary drift is handled at match time by a neighbour-cell lookup, not at key time.

**Store.** Atomic writes with a sibling temp file, per-writer append-only journals with a two-class
fold, an advisory expiring lease, a sync-root probe, and a versioned document that preserves keys it
does not understand.

**Findings.** Five statuses forming a promote-only lattice for machine merges, with the asymmetry
argued in place: losing a "Resolved" costs a wasted minute, hiding an unresolved conflict behind a
green row ends up in the building.

**Clash.** The rule pipeline (suppress and flag → group → assign), grouping rules including a
stable proximity bucket, carry-over with two-pass matching, the New/Persisting/Resolved/Regressed
delta, and cross-test duplicate collapse on a proximity band. The funnel reports every number that
removed a row from the board.

**Sets.** Boolean set algebra compiled to disjunctive normal form — the only shape the host can run
— with negation compiled to a subtraction rather than a negated operator, because those are not the
same set over a model.

**Appearance.** A layer stack with per-property precedence, an explanation of which layer decided
what and which were overruled, and a planner that emits the smallest set of host writes. Overrides
CamelWorks did not author are reported and never cleared.

**openBIM.** BCF 2.1 and 3.0 read and written from one neutral model; IDS read and checked, with a
specification that matches nothing reported as matching nothing rather than passing.

**Data.** Level inference from geometry when the model has none, unit-aware quantity parsing,
takeoff that reports what it could not read, and six model-health checks with scale-free thresholds.

**Report.** A block model with HTML, XLSX and PDF writers. The PDF writer is hand-rolled, including
PNG embedding with alpha split into a soft mask.

---

## Host adapters — built, compile-verified on four years

`CamelWorks.Nav` implements the seam: document, items, view session, viewpoint store and a batched
property-write transaction over the COM bridge. `CamelWorks.Nav.Clash` is a **separate assembly**
because `Autodesk.Navisworks.Clash.dll` ships with Manage and not with Simulate.

Every API member used was checked against the reference assemblies for all four years before being
written. That found the clipping API is JSON in and JSON out, that saved viewpoints have no rename,
that `Document.CurrentViewpoint` is not a `SavedViewpoint`, that `SetUserDefined` is on the COM GUI
property node, and that 2027 does not carry the clash `Tests` property the other three do.

---

## UI — built, compile-verified, partly wired

The ribbon is complete: five panels, twenty-five buttons, matching the spec. Two dock panes, six
switcher entries, sixteen tabs. Find a Tool searches symptom phrasing, not just command names.

**Wired to real services:** Health, Takeoff, Levels & zones. These do real work on a raw model with
nothing configured.

**Frame only, and they say so on screen:** the remaining thirteen tabs, and the immediate/menu/dialog
ribbon commands. Their Core services exist in most cases; what is missing is the screen between the
two. A tab that renders only a title reads as broken, so each states what it will do and that it is
not connected yet.

**No icons.** The buttons render as text. Twenty-five copies of one placeholder glyph would be worse
than none, and icons are a design deliverable rather than something to invent in code.

---

## Known gaps

- **Nothing has been run inside Navisworks.** Compilation on four years is real verification of the
  API surface and no verification at all of behaviour.
- **Thirteen of sixteen tabs are frames.** Listed above.
- **The graph canvas is a frame.** The Dyncamelo node set is not merged in yet.
- **IDS output is not written**, only read — deliberately, since authoring tools produce IDS files
  and nothing in this product needs to.
- **The IDS schema is not validated in CI.** The proxy in the build environment blocks w3.org, which
  the IDS schema imports. BCF and PDF, which this product *writes*, are both checked; IDS is only
  read, so validating our own reading of somebody else's file would prove nothing anyway.
