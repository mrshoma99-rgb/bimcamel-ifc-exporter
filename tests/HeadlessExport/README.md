# Headless export test

Runs the instanced export on a model in a hidden Navisworks (Automation API), with no UI, and
writes `<out>.summary.txt` beside the IFC: element, instance and merged-shape counts, file size,
time, and the exporter's own validator result.

Built against Navisworks Manage 2025. From the repository root:

```bash
N="C:\Program Files\Autodesk\Navisworks Manage 2025"
dotnet build BIMCamel/BIMCamel.csproj -c Release -p:NavisworksYear=2025 -p:NavisworksDir="$N" -p:AssemblyName=BIMCamelDev -o out/dev
dotnet build tests/HeadlessExport/Plugin/HeadlessExport.Plugin.csproj -c Release -o out/harness
dotnet build tests/HeadlessExport/Runner/HeadlessExport.Runner.csproj -c Release -o out/runner
cp "$N/Autodesk.Navisworks.Automation.dll" "$N/Autodesk.Navisworks.Interop.ComApiAutomation.dll" out/runner/
out/runner/BIMCamelHeadlessRunner.exe "$N\Samples\ice stadium.nwd" out/harness/BIMCamelHeadlessExport.dll out/test/ice.ifc 16 IFC4
```

- The fourth argument is the merge threshold: elements with more parts than this are written as
  one shape (element-level instancing). `0` turns it off, which is the old per-fragment output.
- The fifth (schema, `IFC4` or `IFC2X3`) and sixth ("Skip geometry smaller than", in mm) are
  optional.
- The test build is renamed `BIMCamelDev` so it is never confused with the BIMCamel bundle that
  Navisworks loads by itself.
- Model, add-in and output must be under your user profile or a readable install folder:
  Navisworks runs outside any sandbox the caller is in.
