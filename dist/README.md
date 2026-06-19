# Manual install (no installer)

`BIMCamel.bundle/` is a ready-to-copy Navisworks plug-in bundle. To install without the
`BIMCamel_Setup.exe` installer:

1. Drop the built **`BIMCamel.dll`** into `BIMCamel.bundle/Contents/` (next to the `en-US` and
   `Resources` folders). It comes from a Release build: `BIMCamel/bin/Release/net48/BIMCamel.dll`.
2. Copy the whole **`BIMCamel.bundle`** folder into your per-user plug-ins folder:

   ```
   %AppData%\Autodesk\ApplicationPlugins\
   ```

   (i.e. `C:\Users\<you>\AppData\Roaming\Autodesk\ApplicationPlugins\BIMCamel.bundle`)
3. Restart Navisworks — the **BIMCamel** ribbon tab appears.

Works on Navisworks 2024 / 2025 / 2026 (Manage + Simulate), and on any UI language — Navisworks
falls back to the `en-US` ribbon layout. No admin rights needed.
