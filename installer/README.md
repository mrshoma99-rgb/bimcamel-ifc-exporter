# BIMCamel installer

One installer is produced from `BIMCamel.iss`:

| Installer | What it does |
|---|---|
| **BIMCamel_Setup.exe** | Asks at startup whether to install for **all users** (admin, `%ProgramData%\Autodesk\ApplicationPlugins\BIMCamel.bundle`) or **just for me** (no admin, `%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle`). Detects any prior install and offers to remove it. Lets you pick a different folder if the Autodesk ApplicationPlugins folder isn't where it expects. |

The wizard lets the user choose **which Navisworks versions** (2024 / 2025 / 2026) and **which flavour**
(Manage / Simulate) to enable. The selection is written into a matching `PackageContents.xml` at
install time. The single managed DLL works across all versions (Navisworks supplies the API of the
running release), so there's one copy under `…\BIMCamel.bundle\Contents\`.

Navisworks auto-loads any `.bundle` under those `ApplicationPlugins` folders on next launch — no
registry, no manual steps.

## What the installer does

1. **Admin / no-admin choice** — the standard Inno Setup "Install for all users / just me" dialog
   appears first. Admin elevates; no-admin stays per-user. Default target folder follows the choice.
2. **Existing-install detection** — if BIMCamel is already installed (any scope, including a manual
   folder copy), the installer asks: **Yes** to uninstall it now and exit, **No** to upgrade /
   overwrite in place, **Cancel** to abort. Crosses scopes too — e.g. installing per-user when an
   admin install is present prompts to remove the admin one.
3. **Custom plug-ins folder** — the directory page is always shown so the user can browse to a
   different folder. If the default Autodesk ApplicationPlugins parent doesn't exist (atypical
   Navisworks install), the installer warns up front and asks the user to point at the right place.
4. **Uninstall** — registered under Apps & Features as "BIMCamel IFC Exporter"; removes the bundle
   folder. The same EXE handles install and upgrade; uninstall is done via Apps & Features (or by
   re-running the installer and choosing "Yes" at the prior-install prompt).

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
