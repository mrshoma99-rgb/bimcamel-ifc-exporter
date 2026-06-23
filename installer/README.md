# BIMCamel installer

One installer is produced from `BIMCamel.iss`:

| Installer | What it does |
|---|---|
| **BIMCamel_Setup.exe** | Installs **per user** (no admin, no UAC) into the running user's own `%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle` — the path is derived from their Windows profile, never a fixed name. Detects any prior install (including a leftover machine-wide "all users" install from older builds) and offers to remove it. Lets you pick a different folder if the Autodesk ApplicationPlugins folder isn't where it expects. |

> **Per-user only.** Earlier builds offered an "all users / just me" choice. The machine-wide
> (`%ProgramData%`) path proved unreliable — Navisworks would finish installing but not show the
> plug-in — and the admin choice also caused the install-mode dialog to stop reappearing and left
> users unable to uninstall. The installer now always installs for the current user (the location
> Navisworks reliably auto-loads) and can clean up an old all-users install if it finds one.

The wizard lets the user choose **which Navisworks versions** (2024 / 2025 / 2026) and **which flavour**
(Manage / Simulate) to enable. The selection is written into a matching `PackageContents.xml` at
install time.

> **One DLL per version.** A managed plug-in is bound to the Navisworks API it was compiled against
> (2024 = `Api 21.x`, 2025 = `22.x`, 2026 = `23.x`); loading a mismatched DLL fails with
> `PLUGIN_LOAD_07: invalid referenced Navisworks Api version`. So the bundle carries a **separate DLL
> per version** in its own year folder (`…\BIMCamel.bundle\2024\`, `\2025\`, `\2026\`), each with its
> own `en-US\` and `Resources\` (the ribbon XAML and icons resolve relative to the DLL), and the
> manifest points each version at its matching DLL. `build_installers.ps1` builds one DLL per
> Navisworks version it finds on the machine and only packages those.

Navisworks auto-loads any `.bundle` under those `ApplicationPlugins` folders on next launch — no
registry, no manual steps.

## What the installer does

1. **Per-user install** — no admin, no UAC, no install-mode prompt. The bundle goes to the running
   user's own `%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle` (the path is resolved from
   their Windows profile at run time, so it adapts to whoever runs it — never a fixed name).
2. **Navisworks check page** — instead of decision pop-ups, an early page lists which supported
   Navisworks (Manage / Simulate, 2024–2026) it found and where, and **pre-selects those versions**
   so the generated manifest matches what's actually installed (a mismatch is the usual reason the
   plug-in never appears).
3. **Existing-install handling** — a prior BIMCamel install (including a leftover machine-wide "all
   users" install from older builds, or a manual folder copy) is detected and upgraded / removed
   automatically, elevating once only if a system-wide copy must be deleted. It also sweeps the
   legacy single-DLL `Contents\` folder from pre-0.3.0 installs so it can't shadow the per-year DLLs.
4. **Custom plug-ins folder** — the directory page lets the user browse to a different folder if the
   Autodesk ApplicationPlugins folder isn't where it expects.
5. **Post-install verification** — after copying, Setup confirms the payload landed and that the
   chosen versions match a detected Navisworks; if anything's off it explains it on the Finished page
   and saves a shareable log to the Desktop instead of a cryptic error box.
6. **Uninstall** — registered under Apps & Features as "BIMCamel IFC Exporter". Uninstall removes the
   **whole bundle folder**, including the generated `PackageContents.xml` (older builds left that
   behind, so Navisworks kept half-loading the plug-in).

## Build

Prerequisites:
- .NET SDK (to build the plugin) and a Navisworks install for compile-time references. **Each
  Navisworks version you want to ship must be installed** (or its API DLLs available), because the
  plug-in is built once per version.
- **Inno Setup 6 or 7** (free): https://jrsoftware.org/isdl.php

Then, from this folder:

```powershell
./build_installers.ps1
```

This:
1. Builds the plugin in **Release once per Navisworks version** (2024/2025/2026), each against that
   version's API restored from NuGet, and stages the DLLs under `installer\staging\<year>\`.
2. Runs `generate_assets.ps1` to render `assets\wizard_image.bmp`, `assets\wizard_small.bmp`, and
   `assets\bimcamel.ico` from the camel logo PNGs (no extra tools needed).
3. Compiles `BIMCamel_Setup.exe` into `installer\output\` (all three year DLLs are staged from NuGet).

To build a single version manually (e.g. only 2025):

```bat
dotnet build ..\BIMCamel\BIMCamel.csproj -c Release -p:NavisworksYear=2025
mkdir staging\2025
copy ..\BIMCamel\bin\Release\net48\BIMCamel.dll staging\2025\BIMCamel.dll
powershell -ExecutionPolicy Bypass -File generate_assets.ps1
iscc BIMCamel.iss
```

## Notes

- **Manual fallback (zero tooling):** the plug-in is just a folder. A ready-to-copy `BIMCamel.bundle`
  skeleton lives in `..\dist\` — drop your per-version DLLs into its `2024\` / `2025\` / `2026\`
  folders and copy the whole bundle into `%AppData%\Autodesk\ApplicationPlugins\`. No admin, no
  installer. The installer exists to make that selectable + clean, with a proper uninstall entry.
- The dev build (`Debug`) auto-deploys to the matching year folder of the per-user bundle for quick
  iteration (set `-p:NavisworksYear=...` to target a specific version); **Release** does not (it's the
  installer's payload source).
- `installer\assets\` and `installer\staging\` are generated at build time and ignored by git.
