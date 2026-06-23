# Manual install (no installer)

`BIMCamel.bundle/` is a ready-to-copy Navisworks plug-in bundle.

> **Important — the DLL is version-specific.** A `BIMCamel.dll` is bound to the Navisworks API
> version it was compiled against (2024 = 21.x, 2025 = 22.x, 2026 = 23.x). Loading a 2024-built DLL
> into 2025/2026 fails with `PLUGIN_LOAD_07: invalid referenced Navisworks Api version`. That's why
> each Navisworks version has its **own** folder (`2024/`, `2025/`, `2026/`) with its own DLL.

## Steps

1. For each Navisworks version you use, build the DLL against **that** version and drop it into the
   matching folder:

   ```powershell
   # 2024 -> 2024\BIMCamel.dll
   dotnet build BIMCamel\BIMCamel.csproj -c Release -p:NavisworksDir="C:\Program Files\Autodesk\Navisworks Manage 2024"
   # 2025 -> 2025\BIMCamel.dll
   dotnet build BIMCamel\BIMCamel.csproj -c Release -p:NavisworksDir="C:\Program Files\Autodesk\Navisworks Manage 2025"
   # 2026 -> 2026\BIMCamel.dll
   dotnet build BIMCamel\BIMCamel.csproj -c Release -p:NavisworksDir="C:\Program Files\Autodesk\Navisworks Manage 2026"
   ```

   Each build writes `BIMCamel\bin\Release\net48\BIMCamel.dll`; copy it to the matching year folder
   here (e.g. `dist\BIMCamel.bundle\2025\BIMCamel.dll`). You only need the versions you actually run;
   delete the year folders (and their components in `PackageContents.xml`) you don't ship.

2. Copy the whole **`BIMCamel.bundle`** folder into your per-user plug-ins folder:

   ```
   %AppData%\Autodesk\ApplicationPlugins\
   ```

   (i.e. `C:\Users\<you>\AppData\Roaming\Autodesk\ApplicationPlugins\BIMCamel.bundle`)

3. Restart Navisworks — the **BIMCamel** ribbon tab appears.

No admin rights needed; works on any UI language (Navisworks falls back to the `en-US` ribbon layout).

## Final layout

```
BIMCamel.bundle\
  PackageContents.xml
  2024\  BIMCamel.dll  en-US\BIMCamel.xaml  Resources\*.png
  2025\  BIMCamel.dll  en-US\BIMCamel.xaml  Resources\*.png
  2026\  BIMCamel.dll  en-US\BIMCamel.xaml  Resources\*.png
```
