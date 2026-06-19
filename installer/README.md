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
install time. The single managed DLL works across all versions (Navisworks supplies the API of the
running release), so there's one copy under `…\BIMCamel.bundle\Contents\`.

Navisworks auto-loads any `.bundle` under those `ApplicationPlugins` folders on next launch — no
registry, no manual steps.

## What the installer does

1. **Per-user install** — no admin, no UAC, no install-mode prompt. The bundle goes to the running
   user's own `%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle` (the path is resolved from
   their Windows profile at run time, so it adapts to whoever runs it — never a fixed name).
2. **Existing-install detection** — if BIMCamel is already installed (including a leftover
   machine-wide "all users" install from older builds, or a manual folder copy), the installer asks:
   **Yes** to uninstall it now and exit, **No** to upgrade / overwrite in place, **Cancel** to abort.
   Removing a machine-wide install elevates (one UAC prompt) since that's the only step that needs it.
3. **Custom plug-ins folder** — the directory page is always shown so the user can browse to a
   different folder. If their Autodesk ApplicationPlugins parent doesn't exist yet, the installer
   warns up front and offers to create the default.
4. **Uninstall** — registered under Apps & Features as "BIMCamel IFC Exporter". Uninstall removes the
   **whole bundle folder**, including the generated `PackageContents.xml` (older builds left that
   behind, so Navisworks kept half-loading the plug-in). The same EXE handles install and upgrade;
   uninstall is done via Apps & Features or by re-running the installer and choosing "Yes" at the
   prior-install prompt.

## Build

Prerequisites:
- .NET SDK (to build the plugin) and a Navisworks API install for compile-time references
  (the project defaults to `C:\Program Files\Autodesk\Navisworks Manage 2024`).
- **Inno Setup 6** (free): https://jrsoftware.org/isdl.php

Then, from this folder:

```powershell
./build_installers.ps1
```

This:
1. Builds the plugin in **Release**.
2. Runs `generate_assets.ps1` to render `assets\wizard_image.bmp`, `assets\wizard_small.bmp`, and
   `assets\bimcamel.ico` from the camel logo PNGs (no extra tools needed).
3. Compiles `BIMCamel_Setup.exe` into `installer\output\`.

To compile manually instead:

```bat
dotnet build ..\BIMCamel\BIMCamel.csproj -c Release
powershell -ExecutionPolicy Bypass -File generate_assets.ps1
iscc BIMCamel.iss
```

## Notes

- **Manual fallback (zero tooling):** the plugin is just a folder. You can copy a built
  `BIMCamel.bundle` directly into `%AppData%\Autodesk\ApplicationPlugins\` by hand — no admin, no
  installer — and Navisworks will pick it up. The installer exists to make that selectable + clean,
  with a proper uninstall entry.
- The dev build (`Debug`) auto-deploys to the per-user bundle for quick iteration; **Release** does
  not (it's the installer's payload source).
- `installer\assets\` is generated at build time and ignored by git.
