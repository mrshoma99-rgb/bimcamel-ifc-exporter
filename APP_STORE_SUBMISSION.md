# Publishing BIMCamel to the Autodesk App Store (Navisworks)

A step-by-step guide and a ready-to-paste listing kit for submitting **BIMCamel IFC
Exporter** to the [Autodesk App Store](https://apps.autodesk.com/NAVIS/en/Home/).

> **The one thing that changes the plan:** the App Store does **not** accept a
> self-extracting `.exe`. You upload your plug-in as a **single ZIP of the
> `BIMCamel.bundle` folder**, and **Autodesk generates the MSI installer for you**
> during review. Our Inno Setup `BIMCamel_Setup.exe` stays as the GitHub /
> direct-download installer — it is *not* what goes to the store.

---

## 1. How the store works (the short version)

1. You sign in at <https://apps.autodesk.com/> with an Autodesk ID and fill in the
   **Publisher Corner** (company info, support contact — PayPal only if you ever
   charge; BIMCamel is free, so no PayPal needed).
2. You click **Publish a New Product**, accept the Publisher Agreement, choose
   **Desktop-based App → Win64 → English**.
3. You fill in the **App Submission Form** (description, screenshots, icon, help
   text, support info) and upload **one ZIP** containing the `.bundle` + a help
   file.
4. You select **App Compatibility** — tick Navisworks Manage **and** Simulate for
   2024 / 2025 / 2026 / 2027 — and up to 4 categories per product.
5. You **Submit**. A reviewer contacts you within **24–48 h**. Approval typically
   takes **up to ~2 weeks**. Autodesk tests the app, builds the MSI, and publishes.

There is **no fee** to publish a free app.

---

## 2. Who does what

Legend: ✅ already done / I prepared it · 🟡 needs a Windows + Navisworks machine ·
🔴 only you can do (account / legal / business decision).

| # | Step | Who | Notes |
|---|------|-----|-------|
| 1 | Decide publisher identity (you personally vs. a "BIMCamel" company) | 🔴 You | Drives the publisher name shown on the store. |
| 2 | Create / use an **Autodesk ID** and accept the **Publisher Agreement** | 🔴 You | Legal acceptance — can't be delegated. |
| 3 | Fill **Publisher Corner** (name, email, support contact, company URL, logo) | 🔴 You | Copy in §5 below; logo = your camel mark. |
| 4 | Confirm licensing is store-compatible (MIT, no GPL) | ✅ | MIT is fine. GPL would be rejected — we're clear. |
| 5 | Write the **store listing copy** (description, usage, support, known issues) | ✅ | Drafted in §6 — paste straight in. |
| 6 | Produce the **120×120 app icon** | 🔴 You/designer | Specs in §7. Needs a hi-res source (we only have 16/32 px in-repo). |
| 7 | Capture **screenshots** (up to 10) of BIMCamel in Navisworks | 🟡 You | Requires running Navisworks. Shot list in §7. |
| 8 | Write the **quick-start help file** (bundled in the ZIP) | ✅ | Drafted in §8 — save as PDF/HTML and include. |
| 9 | Verify `PackageContents.xml` is store-compliant | ✅ | Reviewed in §9 — one gap noted (missing `2027/` folder in `dist/`). |
| 10 | Build the **per-year `BIMCamel.dll`** (2024/25/26/27, Release) | 🟡 You | Must build on Windows against each year's API. |
| 11 | Assemble + **ZIP the `.bundle`** (DLLs + help file) | 🟡 You | One zip, ≤600 MB, `.zip` extension. Recipe in §10. |
| 12 | Smoke-test the bundle on a real Navisworks before submitting | 🟡 You | Ribbon tab appears, export works. |
| 13 | Run through the portal form, attach ZIP + icon + screenshots, **Submit** | 🔴 You | §3 walkthrough. |
| 14 | Respond to the reviewer; do the **Test Download** in Publisher Corner | 🔴 You | Reviewer emails within 24–48 h. |

**Bottom line:** I can hand you everything that's text, structure, and policy
(rows 4, 5, 8, 9 + this guide). The three things that genuinely need *you* are: a
hi-res **icon**, **screenshots** + a **build/test** on a Windows+Navisworks box,
and the **account/legal** steps in the portal.

---

## 3. The portal walkthrough (desktop app)

1. Go to <https://apps.autodesk.com/> → sign in (top-right) with your Autodesk ID.
2. Open **Publisher Corner** (under your name menu).
3. **Publisher Settings → Publisher Information**: First/Last name, **Email**
   (gets download + reviewer notifications), **Company URL** = `https://www.bimcamel.com`,
   **Support Contact** (an email or web page you monitor). Save.
   - **Payment Platform / PayPal:** skip — only required for paid apps.
   - **Download Notification:** choose email frequency. Save.
4. **My Page**: upload the company logo, add a short "about BIMCamel" blurb. (Cosmetic, but it's your storefront.)
5. Click **Publish a New Product** → read + **I agree** to the Publisher Agreement → **Continue**.
6. **Desktop-based App** → OS **Win64** → Language **English** → **Continue**.
7. Fill the **App Submission Form** (use §6 copy). Upload the **ZIP** under *App File*,
   the **icon** under *App Icon*, and your **screenshots**.
8. **Price**: choose **Free**.
9. **App Compatibility**: expand Navisworks and tick **Manage + Simulate** for
   **2024, 2025, 2026, 2027**. Pick up to **4 categories** (suggestions in §6).
10. **Summary → Preview** → if happy, **Submit**.
11. Within 24–48 h a reviewer emails you. Use **Publisher Corner → Apps →
    Unpublished → (app) → Download Now** to test the download before it goes live.
    (If you don't hear back in 48 h, email `appsubmissions@autodesk.com`.)

---

## 4. Technical requirements (and how BIMCamel already meets them)

| Requirement | Status |
|---|---|
| .NET API plugin compiled per major version (2024=v21 … 2027=v24) | ✅ We build one DLL per year |
| **.NET Framework 4.8** for Navisworks 2024+ | ✅ Project targets `net48` |
| Bundle installs to `%AppData%\Autodesk\ApplicationPlugins` and runs on launch — no manual copy/register | ✅ Standard `.bundle` + `PackageContents.xml` |
| Each installed **DLL name unique** within a Navisworks session | ✅ `BIMCamel.dll` is distinctive |
| No undocumented API calls, no crashes/instability | ✅ Uses public `Autodesk.Navisworks.Api` + ComApi |
| Works as a complete (non-beta) product | ✅ v0.5.0 is feature-complete |
| Licensing compatible (no GPL) | ✅ MIT |
| Submitted as **ZIP**, not a self-extracting EXE | ⚠️ Build the ZIP per §10 (don't submit `BIMCamel_Setup.exe`) |

---

## 5. Publisher Corner copy (paste-ready)

- **Company URL:** `https://www.bimcamel.com`
- **Support Contact:** *(your support email or `https://www.bimcamel.com`)*
- **My Page blurb:**
  > BIMCamel builds fast, free, open-source BIM tools. Our Navisworks → IFC exporter
  > fills the gap Navisworks leaves open: a quick, zero-config, memory-bounded export
  > to IFC4 and IFC2x3 that produces small, correctly-placed files. More tools and
  > docs at www.bimcamel.com.

---

## 6. App listing copy (paste-ready)

**App Name**
```
BIMCamel IFC Exporter
```

**Short description**
```
Fast, free, open-source Navisworks → IFC exporter (IFC4 & IFC2x3). Zero-config first-click export, small files, correct placement.
```

**App Description** (≤4000 chars; light HTML/bullets allowed)
```
BIMCamel is a free, open-source IFC exporter for Autodesk Navisworks. Navisworks has
no native IFC export — BIMCamel fills that gap without the slowness and setup friction
of commercial tools. The first click produces a valid, correctly-placed IFC with no
configuration required.

KEY FEATURES
• Dual schema — IFC4 (IfcTriangulatedFaceSet) and IFC2x3 (IfcFaceBasedSurfaceModel),
  with the full IFC2x3 MEP vocabulary (IfcFlowSegment / IfcFlowController / …).
• Streaming, memory-bounded engine — exports very large models without out-of-memory
  crashes; peak memory stays in the low hundreds of MB regardless of model size.
• Geometry instancing — repeated parts (bolts, fittings, …) are written once as
  IfcRepresentationMap + IfcMappedItem, keeping files small.
• Flexible scope — whole model, current selection, the active section box, a saved /
  search set, or batch mode: multiple sets → one IFC each.
• Size splitting — cap output size (e.g. 200 MB); larger exports roll into
  name_001.ifc, name_002.ifc, … each a complete, standalone IFC. Composes with batch.
• Property sets — category-qualified, typed values, with content dedup and optional
  renaming / relocation into standard Psets.
• Object → IFC class mapping — assign Navisworks sets to IFC classes (with optional
  PredefinedType); unmapped elements stay IfcBuildingElementProxy.
• Type objects, materials, classification, base quantities (volume / area / length
  from the mesh), and multi-storey spatial structure from a Level property.
• Coordinates & georeferencing — base-point modes with live preview; IFC4
  IfcMapConversion, IFC2x3 baked placement. Split parts and batch files share one
  origin so they overlay perfectly.
• Reporting — element / triangle counts, file size, per-entity-type size profile,
  phase-timing breakdown, and peak memory. Export profiles save / load every setting
  as JSON.
• Pure managed, no third-party runtime dependency.

GETTING STARTED
After install, restart Navisworks and open the BIMCamel ribbon tab → IFC exporter.
Pick a schema and scope (default: IFC4, whole model), click Export IFC, choose a
path. Done.

TIP: An active DataTools / external-database link runs a query per object and can add
many minutes to an export — deactivate it under Home → DataTools before exporting.

Works on Navisworks Manage and Simulate, 2024 / 2025 / 2026 / 2027 (Win64).
Open-source under the MIT license. Not affiliated with Autodesk.
More tools, updates and docs: www.bimcamel.com
```

**General Usage Instructions** (workflow field)
```
1. Open Navisworks and load your model.
2. Click the BIMCamel ribbon tab → IFC exporter to open the panel.
3. Choose a schema (IFC4 or IFC2x3) and a scope (whole model, current selection,
   active section box, or a saved/search set). Defaults are IFC4 + whole model.
4. Optionally pick a quality preset (Small file / Balanced / High detail), map
   Navisworks sets to IFC classes, and set the base point for georeferencing.
5. Click Export IFC and choose an output path.
6. (Optional) Cap the output file size to auto-split into standalone parts, or use
   batch mode to export multiple sets to one IFC each.
Before exporting, deactivate any active DataTools / external-database link
(Home → DataTools) to avoid a per-object query slowdown.
```

**Support Information**
```
Support and documentation: www.bimcamel.com
Source code and issue tracker: https://github.com/mrshoma99-rgb/bimcamel-ifc-exporter
Email: <your support email here>
BIMCamel is free and open-source (MIT). Issues and feature requests are welcome on
the GitHub tracker.
```

**Installation / Uninstallation** (when Autodesk builds the MSI for you)
```
standard text
```
*(Per Autodesk's instructions: when they generate the installer for you, just put the
literal words `standard text` here and they substitute the correct install/uninstall
wording.)*

**Known Issues**
```
• An active DataTools / external-database link issues one query per object and can
  add many minutes to an export (and floods the console with DATATOOLS_SQL_EXEC
  errors if the link is broken). Deactivate it under Home → DataTools before
  exporting; the panel also shows an up-front reminder.
• A plug-in DLL only loads in the Navisworks version it was compiled against; the
  bundle ships a separate build per year (2024/2025/2026/2027) to cover this.
```

**Suggested categories** (pick up to 4 that the store offers for Navisworks)
```
Translator / Interoperability · Data / Import-Export · BIM · Productivity
```
*(The exact category list is shown in the portal at compatibility time — choose the
closest matches; "Translator" / interoperability is the primary one.)*

**Compatibility to tick:** Navisworks **Manage** and **Simulate**, **2024, 2025,
2026, 2027** (only tick a version once you've actually tested the build for it).

---

## 7. Icon & screenshots (you / a designer)

**App icon**
- Recommended **120×120 px**, PNG/GIF/JPG, ≤2 MB, 72/96 DPI.
- Must have a visible **border/frame** and be legible — Autodesk rejects tiny,
  unframed, or unreadable icons.
- Don't put the app/company name on it (shown next to the icon already).
- Source: the camel mark. We only have `camel_16.png` / `camel_32.png` in-repo —
  too small to upscale cleanly. Re-export the logo from its vector/hi-res master at
  120×120 (or have a designer do a quick framed version).

**Screenshots** (up to 10; ≤2000×2000 px, ≤20 MB each) — requires Navisworks. Good shot list:
1. The BIMCamel ribbon tab in Navisworks.
2. The IFC exporter panel with schema + scope options.
3. Quality presets (Small file / Balanced / High detail).
4. Object → IFC class mapping dialog.
5. A property-set / Pset configuration view.
6. The base-point / georeferencing preview.
7. The post-export report (counts, size profile, timings, peak memory).
8. An exported IFC opened in a viewer next to the Navisworks model (proof of correct placement).
9. Batch / size-splitting settings.
10. The About panel showing version + bimcamel.com.

A YouTube demo link is optional but recommended.

---

## 8. Quick-start help file (bundle this in the ZIP)

Autodesk asks for a help/quick-start file inside the ZIP (txt / doc / html / pdf,
unzipped/plain). Save the following as `BIMCamel_QuickStart.pdf` (or `.html`) and
include it next to `PackageContents.xml`:

```
BIMCamel IFC Exporter — Quick Start

WHAT IT DOES
Exports your Autodesk Navisworks model to IFC (IFC4 or IFC2x3). Navisworks has no
native IFC export; BIMCamel adds it with a fast, memory-bounded engine and a
zero-config first-click export.

REQUIREMENTS
Autodesk Navisworks Manage or Simulate 2024, 2025, 2026 or 2027 (Win64),
with .NET Framework 4.8 (installed with Navisworks).

AFTER INSTALL
Restart Navisworks. A "BIMCamel" tab appears on the ribbon with two buttons:
IFC exporter and About. If the tab is missing, check Tools/Plug-ins to confirm
BIMCamel loaded for your Navisworks version.

EXPORT IN 3 STEPS
1. BIMCamel tab → IFC exporter.
2. Pick a schema (IFC4 default) and scope (whole model default). Everything else
   has sensible defaults.
3. Click Export IFC, choose a path. Done.

USEFUL OPTIONS
• Scope: whole model, current selection, active section box, a saved/search set, or
  batch (multiple sets → one IFC each).
• Quality presets: Small file / Balanced / High detail.
• Object → IFC class mapping; property-set mapping & dedup; base quantities;
  multi-storey from a Level property.
• Georeferencing: base-point modes with live preview (IFC4 IfcMapConversion;
  IFC2x3 baked placement).
• Size splitting: cap output size; large exports roll into name_001.ifc, name_002.ifc…
• Export profiles: save/load all settings as JSON.

PERFORMANCE TIP
Deactivate any active DataTools / external-database link (Home → DataTools) before
exporting — it runs one query per object and can add many minutes.

SUPPORT
www.bimcamel.com · https://github.com/mrshoma99-rgb/bimcamel-ifc-exporter
```

---

## 9. PackageContents.xml — store-compliance review

Current `dist/BIMCamel.bundle/PackageContents.xml` is **structurally correct** for the
store:

- `SchemaVersion="3.0"`, `AppType="ManagedPlugin"`, `OS="Win64"` ✅
- One component per **Manage (NAVMAN)** and **Simulate (NAVSIM)** per year ✅
- Correct series mapping: 2024=Nw21, 2025=Nw22, 2026=Nw23, 2027=Nw24 ✅
- `Name`, `Author`, `CompanyDetails`/`Url`, `ProductCode` GUID present ✅

**Action items before zipping:**
1. **Add the missing `2027/` folder.** `dist/BIMCamel.bundle/` has `2024/ 2025/ 2026/`
   but **no `2027/`**, while `PackageContents.xml` points `.\2027\BIMCamel.dll`. Create
   `2027/` (with its `en-US/` + `Resources/`) and drop the 2027 build in, or the 2027
   components reference a file that isn't there.
2. **Bump `Version`** in `PackageContents.xml` to match the release you submit.
3. **Confirm each year's `BIMCamel.dll`** is the Release build compiled against *that*
   year's API (a wrong-year DLL loads but the ribbon silently won't appear).

---

## 10. Building the store ZIP (Windows)

```powershell
# 1. Build Release for every supported year (each against its own NuGet API)
foreach ($y in 2024,2025,2026,2027) {
    dotnet build BIMCamel\BIMCamel.csproj -c Release -p:NavisworksYear=$y
    Copy-Item "BIMCamel\bin\Release\net48\BIMCamel.dll" "dist\BIMCamel.bundle\$y\BIMCamel.dll"
}

# 2. Add the quick-start help file next to PackageContents.xml
Copy-Item BIMCamel_QuickStart.pdf dist\BIMCamel.bundle\

# 3. Remove the PLACE_*.txt placeholders, then zip the bundle FOLDER (not its contents)
Get-ChildItem dist\BIMCamel.bundle -Recurse -Filter PLACE_*.txt | Remove-Item
Compress-Archive -Path dist\BIMCamel.bundle -DestinationPath BIMCamel.bundle.zip
```

The resulting `BIMCamel.bundle.zip` (a zipped `.bundle` folder + help file, **no
EXE**, ≤600 MB) is what you upload under *App File*. Autodesk turns it into the MSI.

> Smoke-test first: unzip `BIMCamel.bundle` into
> `%AppData%\Autodesk\ApplicationPlugins\`, restart Navisworks, confirm the ribbon tab
> appears and an export succeeds — for **every** year you intend to tick.

---

## 11. Sources

- [Autodesk App Store — Navisworks publisher requirements](https://aps.autodesk.com/app-store/publisher-center/navisworks)
- [Autodesk App Store — Publisher Center / Getting Started](https://aps.autodesk.com/app-store/publisher-center)
- [Desktop App Submission Process Overview (PDF)](https://damassets.autodesk.net/content/dam/autodesk/www/adn/pdf/desktop-app-submission-process-overview.pdf)
- [Publisher Product Guidelines](https://apps.autodesk.com/Publisher/ProductGuidelines)
- [Publisher FAQ (PDF)](https://damassets.autodesk.net/content/dam/autodesk/www/adn/pdf/frequently-asked-questions.pdf)
- [Autodesk App Store Publisher Agreement (PDF)](https://apps.autodesk.com/Content/pdf/Publisher.pdf)
- [Navisworks store front](https://apps.autodesk.com/NAVIS/en/Home/)
