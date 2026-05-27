# BIMCamel installers

Two installers are produced from one Inno Setup script (`BIMCamel.iss`):

| Installer | Privileges | Installs to | Scope |
|---|---|---|---|
| **BIMCamel_Setup.exe** | **Admin** (elevates) | `%ProgramData%\Autodesk\ApplicationPlugins\BIMCamel.bundle` | All users on the machine |
| **BIMCamel_Setup_NoAdmin.exe** | **None** (`lowest`) | `%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle` | Current user only |

Both let the user choose **which Navisworks versions** (2024 / 2025 / 2026) and **which flavour**
(Manage / Simulate) to enable. The selection is written into a matching `PackageContents.xml` at
install time. The single managed DLL works across all versions (Navisworks supplies the API of the
running release), so there's one copy under `…\BIMCamel.bundle\Contents\`.

Navisworks auto-loads any `.bundle` under those `ApplicationPlugins` folders on next launch — no
registry, no manual steps.

## Build

Prerequisites:
- .NET SDK (to build the plugin) and a Navisworks API install for compile-time references
  (the project defaults to `C:\Program Files\Autodesk\Navisworks Manage 2024`).
- **Inno Setup 6** (free): https://jrsoftware.org/isdl.php

Then, from this folder:

```powershell
./build_installers.ps1
```

This builds the plugin in **Release** and compiles both EXEs into `installer\output\`.

To compile manually instead:

```bat
dotnet build ..\BIMCamel\BIMCamel.csproj -c Release
iscc BIMCamel.iss            REM admin / machine-wide
iscc /DNOADMIN BIMCamel.iss  REM no-admin / per-user
```

## Notes

- **Uninstall:** both register an uninstaller (Apps & features → "BIMCamel IFC Exporter") that
  removes the bundle folder.
- **No-admin caveat:** if the machine-wide (admin) bundle is *also* present, Navisworks loads both;
  prefer one. The uninstaller removes only the edition that was installed.
- **Manual fallback (zero tooling):** the plugin is just a folder. You can copy a built
  `BIMCamel.bundle` directly into `%AppData%\Autodesk\ApplicationPlugins\` by hand — no admin, no
  installer — and Navisworks will pick it up. The installers exist to make that selectable + clean.
- The dev build (`Debug`) auto-deploys to the per-user bundle for quick iteration; **Release** does
  not (it's the installer's payload source).
