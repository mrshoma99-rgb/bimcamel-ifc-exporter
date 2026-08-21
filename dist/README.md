# Manual install (no installer)

`BIMCamel.bundle/` is a ready-to-copy Navisworks plug-in bundle carrying both products —
**CamelWorks** (coordination, data and delivery) and the **BIMCamel IFC exporter**. To install
without `BIMCamelSetup.exe`:

1. Build **against the Navisworks year you run** and drop the results into the matching year folder
   (`2024/`, `2025/`, `2026/`, or `2027/`), next to that folder's `en-US/` and `Resources/` folders:

   ```
   dotnet build BIMCamel/BIMCamel.csproj -c Release -p:NavisworksYear=2025
   dotnet build CamelWorks/CamelWorks.UI/CamelWorks.UI.csproj -c Release -p:NavisworksYear=2025
   dotnet build CamelWorks/CamelWorks.Nav.Clash/CamelWorks.Nav.Clash.csproj -c Release -p:NavisworksYear=2025
   ```

   Copy into `BIMCamel.bundle/2025/`:

   | File | From |
   |---|---|
   | `BIMCamel.dll` | `BIMCamel/bin/Release/net48/` |
   | `CamelWorks.UI.dll` | `CamelWorks/CamelWorks.UI/bin/Release/net48/` |
   | `CamelWorks.Nav.dll` | `CamelWorks/CamelWorks.Nav/bin/Release/net48/` |
   | `CamelWorks.Nav.Clash.dll` | `CamelWorks/CamelWorks.Nav.Clash/bin/Release/net48/` |
   | `CamelWorks.Core.dll` | `CamelWorks/CamelWorks.Core/bin/Release/netstandard2.0/` |

   and `CamelWorks/CamelWorks.UI/CamelWorks.xaml` into `BIMCamel.bundle/2025/en-US/`. The
   Navisworks API is restored from NuGet, so no Navisworks install is needed to build.

   You only need the year folder(s) for the version(s) you actually have; leave the rest empty.

   **`CamelWorks.Nav.Clash.dll` is not declared in `PackageContents.xml`, and that is deliberate.**
   It is the only assembly that needs `Autodesk.Navisworks.Clash.dll`, which ships with Navisworks
   Manage and not with Simulate. CamelWorks loads it by name the moment a clash screen is opened, so
   a Simulate user gets a screen saying the clash engine is not in their edition rather than a
   FileNotFoundException naming a DLL they have never heard of.

2. Copy the whole **`BIMCamel.bundle`** folder into your per-user plug-ins folder:

   ```
   %AppData%\Autodesk\ApplicationPlugins\
   ```

   (i.e. `C:\Users\<you>\AppData\Roaming\Autodesk\ApplicationPlugins\BIMCamel.bundle`)
3. Restart Navisworks — the **CamelWorks** and **BIMCamel** ribbon tabs appear. (The BIMCamel tab is
   shared with the other BIMCamel tools: if [Dyncamelo](https://github.com/mrshoma99-rgb/dyncamelo)
   is installed too, its buttons sit on the same tab — both ribbon layouts declare the same
   `ID_Tab_BIMCamel` tab id.)

## Why one DLL per year

A Navisworks plug-in must be **compiled against the API of the release it runs in** — 2024 uses
`Autodesk.Navisworks.Api` **v21**, 2025 uses **v22**, 2026 uses **v23**, 2027 uses **v24**. A single DLL built against
one year still _loads_ in the others (it shows up in the Plugin Manager), but its **ribbon tab
silently fails to appear**, because Navisworks reflects over the `[RibbonLayout]`/`[RibbonTab]`/
`[Command]` attributes and those attribute types don't resolve across a major API version. That's why
each Navisworks year gets its own folder and its own matching build of every plug-in DLL.

Works on Navisworks 2024 / 2025 / 2026 / 2027 (Manage + Simulate), and on any UI language — Navisworks falls
back to the `en-US` ribbon layout. No admin rights needed.
