; BIMCamel IFC Exporter — Inno Setup installer
; Builds TWO editions from this one script:
;   • Admin (machine-wide):   iscc BIMCamel.iss
;       → installs to %ProgramData%\Autodesk\ApplicationPlugins\BIMCamel.bundle  (all users, needs admin)
;   • No-admin (per-user):    iscc /DNOADMIN BIMCamel.iss
;       → installs to %AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle      (current user, no admin)
;
; Prerequisite: build the plugin in Release first (build_installers.ps1 does this for you):
;   dotnet build ..\BIMCamel\BIMCamel.csproj -c Release
;
; The user picks which Navisworks versions (2024/2025/2026) and which flavour (Manage / Simulate);
; the matching PackageContents.xml is generated at install time (see [Code]). The single managed DLL
; works across all versions — Navisworks loads the API from whichever release is running.

#define AppName "BIMCamel IFC Exporter"
#define AppVer "0.1.0"
#define DllSrc "..\BIMCamel\bin\Release\net48\BIMCamel.dll"

[Setup]
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=BIMCamel
AppPublisherURL=https://bimcamel.com
AppSupportURL=https://bimcamel.com
WizardStyle=modern
DisableDirPage=yes
DisableProgramGroupPage=yes
Uninstallable=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
#ifdef NOADMIN
  PrivilegesRequired=lowest
  OutputBaseFilename=BIMCamel_Setup_NoAdmin
  DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\BIMCamel.bundle
#else
  PrivilegesRequired=admin
  OutputBaseFilename=BIMCamel_Setup
  DefaultDirName={commonappdata}\Autodesk\ApplicationPlugins\BIMCamel.bundle
#endif

[Types]
Name: "full";   Description: "All Navisworks versions (Manage + Simulate)"
Name: "custom"; Description: "Choose versions / flavour"; Flags: iscustom

[Components]
Name: "n2024man"; Description: "Navisworks Manage 2024";   Types: full custom
Name: "n2024sim"; Description: "Navisworks Simulate 2024"; Types: full
Name: "n2025man"; Description: "Navisworks Manage 2025";   Types: full custom
Name: "n2025sim"; Description: "Navisworks Simulate 2025"; Types: full
Name: "n2026man"; Description: "Navisworks Manage 2026";   Types: full custom
Name: "n2026sim"; Description: "Navisworks Simulate 2026"; Types: full

[Files]
; The managed plugin + its content (one shared copy; manifest below targets the chosen versions).
Source: "{#DllSrc}";                  DestDir: "{app}\Contents";           Flags: ignoreversion
Source: "..\BIMCamel\BIMCamel.xaml";  DestDir: "{app}\Contents\en-US";     Flags: ignoreversion
Source: "..\BIMCamel\Resources\*.png"; DestDir: "{app}\Contents\Resources"; Flags: ignoreversion

[Code]
function CompBlock(Desc, Platform, Series: string): string;
begin
  Result :=
    '  <Components Description="' + Desc + '">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="' + Platform + '" SeriesMin="' + Series + '" SeriesMax="' + Series + '" />' + #13#10 +
    '    <ComponentEntry AppType="ManagedPlugin" ModuleName=".\Contents\BIMCamel.dll" />' + #13#10 +
    '  </Components>' + #13#10;
end;

procedure WriteManifest();
var
  Xml: string;
begin
  Xml :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<ApplicationPackage SchemaVersion="3.0" Version="0.1.0.0" Author="BIMCamel"' + #13#10 +
    '    ProductCode="C3D4E5F6-A7B8-9012-CDEF-234567890123"' + #13#10 +
    '    Name="BIMCamel IFC Exporter"' + #13#10 +
    '    Description="Fast, free Navisworks to IFC exporter (IFC4 / IFC2x3) - bimcamel.com">' + #13#10 +
    '  <CompanyDetails Name="BIMCamel" Url="https://bimcamel.com" />' + #13#10;

  if WizardIsComponentSelected('n2024man') then Xml := Xml + CompBlock('2024 Manage',   'NAVMAN', 'Nw21');
  if WizardIsComponentSelected('n2024sim') then Xml := Xml + CompBlock('2024 Simulate', 'NAVSIM', 'Nw21');
  if WizardIsComponentSelected('n2025man') then Xml := Xml + CompBlock('2025 Manage',   'NAVMAN', 'Nw22');
  if WizardIsComponentSelected('n2025sim') then Xml := Xml + CompBlock('2025 Simulate', 'NAVSIM', 'Nw22');
  if WizardIsComponentSelected('n2026man') then Xml := Xml + CompBlock('2026 Manage',   'NAVMAN', 'Nw23');
  if WizardIsComponentSelected('n2026sim') then Xml := Xml + CompBlock('2026 Simulate', 'NAVSIM', 'Nw23');

  Xml := Xml + '</ApplicationPackage>' + #13#10;
  SaveStringToFile(ExpandConstant('{app}\PackageContents.xml'), Xml, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteManifest();
end;
