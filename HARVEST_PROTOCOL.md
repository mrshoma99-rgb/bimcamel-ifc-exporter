# Harvest protocol — moving code into CamelWorks

CamelWorks is **published source**. Two private repositories are being harvested into it:
`KVI_Tools` (a production Navisworks add-in) and `BIMCamel` (the commercial web product). Both are
owned by the project owner and cleared for use. This document is the gate every harvested file passes
through before it lands in a public repository.

Three rules, from the owner:

1. **No trace of "KVI" anywhere in code, UI, data or filenames.**
2. **Do not port the UI one-to-one.** Same capability, new design, new names.
3. **Harvesting from the web product must not expose anything that weakens its security.**

---

## 1. Security clearance — `BIMCamel` (the web product)

The web product is a **commercial SaaS**: JWT auth, Google OAuth, LemonSqueezy billing, Turnstile,
an admin surface and a server API. Publishing the wrong part of it is not a licensing question, it is
a live-service exposure.

### Scan result (performed before any harvest)

| Check | Result |
|---|---|
| Hardcoded secret values (`key/secret/token/password = "…"`, ≥16 chars) | **none found** |
| `.env` files in git history | only `.env.production.example` and `src/bimcamel-web/.env.production` |
| `.env.production.example` values | **placeholders** — `Jwt__Key` is a worded placeholder, billing/OAuth/SMTP keys are empty |
| `src/bimcamel-web/.env.production` | one var, `VITE_API_URL` — public by design, Vite bakes `VITE_*` into the client bundle |
| Private keys / certs (`*.pem`, `*.key`, `BEGIN … PRIVATE KEY`) | none |

**No live credential is exposed today.** The risk is not secrets — it is publishing *logic*.

### The no-harvest zone — never copied into CamelWorks, in whole or in part

```
cloudflare/                    edge config and bindings
contract/                      the client↔server contract
src/BIMCamel.Api/              the server: auth, billing, entitlement, admin
src/tessellation-service-node/ the geometry service
any file matching: LemonSqueezy · Jwt · OAuth · Turnstile · isAdmin · entitlement   (64 files)
```

Publishing entitlement or licence-gating logic tells a reader exactly how to bypass Pro gating.
Publishing the server contract exposes endpoint shapes and validation assumptions. Neither is needed
for anything CamelWorks does.

### The cleared zone — safe to harvest

`src/bimcamel-web/src/components/viewer/overrides/` and `.../viewer/viewstate/`.

Verified clean: `appearanceOverrides.ts` imports exactly two things — a local types file and a GUID
helper. Zero hits across the module for `fetch(`, `process.env`, `auth`, `credential`, or any API
import. It is **pure client-side state and rendering**, which is why it is the safe piece and also the
piece worth taking.

### Standing rule

Harvest **by file, never by directory**, and re-run the secret scan on the staged diff before the
commit that makes it public. A file that imports from `contract/` or from any server module is
disqualified — port the *design*, not the file.

---

## 2. De-branding — `KVI_Tools`

### Scope

| Where | Count |
|---|---|
| Paths containing `KVI` | 48 files |
| Files containing `KVI` in content | 107 |
| Namespaces | 10 (`KVI_Tools`, `.BetterSets`, `.BimCamel`, `.Clash`, `.Clearance`, `.Core`, `.Data`, `.IDS`, `.Overrides`, `.Viewpoints`) |

### An employer is named in the source

`KVIToolsPlugin.cs:253` reads *"Kanadevia Inova (KVI) or the IT department."* — and **Kanadevia Inova
appears in `PackageContents.xml`, `PackageContents_Clash.xml` and both installer manifests under
`dist/`** as well. "KVI" is that company's initials, not a product name.

Every occurrence must go: the About text, all four `PackageContents.xml` files, the plug-in id, the
ribbon tab id, and the display name. Grep for `KVI`, `Kanadevia` and `Inova` and require zero hits as a
release-gate check.

> The owner has confirmed the code is theirs. Flagging once and moving on: a tool named for an employer
> and referencing their IT department is the shape that attracts a work-for-hire question, and
> publishing under an irrevocable open licence cannot be undone. Worth one look before the first
> public push; not revisited here.

### No compatibility burden — the formats get designed properly

CamelWorks has **no users yet**. Nothing harvested has to stay wire-compatible with anything, and that
is worth spending deliberately rather than letting it pass unnoticed.

Two things in `BetterSets` would otherwise have been permanent constraints:

```csharp
// BetterSets/SetRecipe.cs:21   — written into the COMMENT of saved sets inside the user's NWF/NWD
public const string Marker = "--- KVI Better Sets recipe (edited by the tool) ---";
// BetterSets/BetterSetsStore.cs:61
public const string RecipeAuthor = "KVI Better Sets";
```

That marker is a persisted data format living in customer model files, and `%AppData%\KVI_Tools\`
holds settings, run logs and `team_members.xml`. On a product with a userbase, renaming either means
carrying a dual-read path and a first-run migration forever.

**There is no userbase, so there is no dual-read and no migration.** Both are deleted from the plan.

What to do with the freedom, rather than merely enjoying it:

- **Design the recipe format for what CamelWorks needs**, not what fitted inside a set comment. A set
  comment is a single unstructured string; the recipe was squeezed into it because that was the only
  per-set storage Navisworks offers. CamelWorks has a sidecar store — so the recipe belongs there, with
  the *human-readable* formula still written to the set comment for the reason it was there originally:
  anyone opening the model can see what the set means without the tool. Keep that property, drop the
  encoding hack.
- **One namespace for persisted state.** Every harvested tool arrives with its own `%AppData%` folder
  and file layout. They collapse into the single `.camelworks/` sidecar the plan already defines, with
  one schema version across the product rather than one per tool.
- **Version every format from the first commit.** The compatibility burden that does not exist today
  starts existing on the day of first release, and a `schemaVersion` written from day one costs nothing
  now and buys the migration path later.

The same applies to anything else harvested: **change the data structures freely.** The instruction to
redesign the UI (§3) extends to the formats underneath it.

---

## 3. Redesign, don't port

The owner's instruction: same functionality, **not** the same UI and **not** the same names. KVI_Tools
is 16 WinForms dialogs, one per tool. CamelWorks is a ribbon plus two dock panes with workspaces and
tabs. A one-to-one port would reintroduce exactly the "sixteen unrelated dialogs" problem the
CamelWorks information architecture exists to solve.

**Harvest the engine, rewrite the surface.** The valuable part of every one of these tools is the
logic underneath — the DNF compiler, the IDS facet evaluator, the clearance sweep, the override
resolution order. The forms are the disposable half.

| KVI_Tools | Harvest | CamelWorks home | New name |
|---|---|---|---|
| `BetterSets/` (`SetNode`, `DnfCompiler`, `SearchEmitter`, `SetEvaluator`, `SetFormula`) | **engine** | Sets & Views workspace, new *Build* tab | **Set Builder** — the algebra. *Set Library* stays the store/reuse tab |
| `Overrides/OverrideTransfer` | concept only | folds into the new **Appearance Manager** | — |
| `IDS/` (`IdsParser`, `IdsChecker`, `IdsModels`) | **engine** | Project workspace, new *Requirements* tab | **IDS Check** / **IDS Author** — *IDS is a buildingSMART standard, so the name stays; it is not branding* |
| `Clearance/` (`ClearanceChecker`) | **engine** | Coordinate workspace | **Clearance** |
| `Clash/ClashMatrixBuilder` + `ClashMatrixExcelImport` | **engine** | existing *Tests* tab | **Clash Tests** (already named) |
| `Clash/SphereExporter` + `SphereOverlay` | **engine** | Coordinate | **Clash Markers** |
| `Clash/ClashRenamer`, `SearchSetMakerForm` | **engine** | fold into *Rules* / *Set Builder* | — no standalone tool |
| `Clash/ClashExportXml` + `ClashXmlSchema` + `ClashImporter` | **engine** | *Report* / *BCF* | — |
| `Data/PropertyWriter`, `DataLinker`, `ManualParamInsert` | **engine** | Data workspace | **Data Manager** (already named) |
| `Data/WbsWriter` | **engine** | Data/*Takeoff* | reopens the BOQ cut — panel decides |
| `BimCamel/BatchRunner`, `ModelJob`, `RunLogger`, `NavisworksLocator` | **engine** | Batch workspace | reopens the automation-host cut — panel decides |
| `Viewpoints/ViewpointCreator` | **engine** | Sets & Views/*Viewpoints* | **Viewpoints** (already named) |
| all 16 `*Form.cs` | **discard** | — | rebuilt as pane tabs against the shared dialog kit |

**Naming rule going forward:** a CamelWorks tool is named for what the user is trying to do, not for
what it is made of, and never after a company. No abbreviations in ribbon labels.

---

## 4. Release gate

Add to §8 Definition of done:

- `grep -ri "kvi\|kanadevia\|inova"` over the whole published tree returns **zero** hits, including
  `PackageContents.xml`, installer manifests, resource files and the About dialog.
- The staged diff of every harvested file has passed the secret scan in §1.
- No harvested file imports from `contract/`, `cloudflare/`, `BIMCamel.Api` or any auth/billing module.
- Every persisted format carries a `schemaVersion` from its first commit.
- No harvested tool writes outside the single `.camelworks/` sidecar.
