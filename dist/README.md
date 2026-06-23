# Manual install (no installer)

`BIMCamel.bundle/` is a ready-to-copy Navisworks plug-in bundle. To install without the
`BIMCamel_Setup.exe` installer:

1. Build the plug-in **against the Navisworks year you run** and drop the resulting
   **`BIMCamel.dll`** into the matching year folder (`2024/`, `2025/`, or `2026/`), next to that
   folder's `en-US/` and `Resources/` folders:

   ```
   dotnet build BIMCamel/BIMCamel.csproj -c Release ^
       -p:NavisworksDir="C:\Program Files\Autodesk\Navisworks Manage 2025"
   ```

   then copy `BIMCamel/bin/Release/net48/BIMCamel.dll` into `BIMCamel.bundle/2025/`.

   You only need the year folder(s) for the version(s) you actually have; leave the rest empty.

2. Copy the whole **`BIMCamel.bundle`** folder into your per-user plug-ins folder:

   ```
   %AppData%\Autodesk\ApplicationPlugins\
   ```

   (i.e. `C:\Users\<you>\AppData\Roaming\Autodesk\ApplicationPlugins\BIMCamel.bundle`)
3. Restart Navisworks — the **BIMCamel** ribbon tab appears.

## Why one DLL per year

A Navisworks plug-in must be **compiled against the API of the release it runs in** — 2024 uses
`Autodesk.Navisworks.Api` **v21**, 2025 uses **v22**, 2026 uses **v23**. A single DLL built against
one year still _loads_ in the others (it shows up in the Plugin Manager), but its **ribbon tab
silently fails to appear**, because Navisworks reflects over the `[RibbonLayout]`/`[RibbonTab]`/
`[Command]` attributes and those attribute types don't resolve across a major API version. That's why
each Navisworks year gets its own folder and its own matching build of `BIMCamel.dll`.

Works on Navisworks 2024 / 2025 / 2026 (Manage + Simulate), and on any UI language — Navisworks falls
back to the `en-US` ribbon layout. No admin rights needed.
