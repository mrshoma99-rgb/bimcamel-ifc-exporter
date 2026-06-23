; BIMCamel IFC Exporter — per-user Inno Setup installer
;
; One installer (BIMCamel_Setup.exe), per-user (local) install only:
;   * Installs to the current user's Autodesk ApplicationPlugins folder
;     (%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle) — no admin, no UAC.
;     This is the location Navisworks reliably auto-loads for the logged-in user, and the
;     path is derived from the running user's profile (never a fixed/developer name).
;   * Detects a previous BIMCamel install — including a leftover machine-wide ("all users")
;     install from older builds — and offers to uninstall it (elevating when required) or
;     upgrade in place.
;   * Registers a proper uninstaller (Apps & Features) that removes the whole bundle, including
;     the generated PackageContents.xml, so nothing is left behind for Navisworks to half-load.
;   * Lets the user change the destination folder when the default Autodesk ApplicationPlugins
;     folder isn't where it should be (browse button on the directory page).
;   * Carries the BIMCamel logo on the wizard and a "Visit www.bimcamel.com" task.
;
; Build (after `dotnet build ..\BIMCamel\BIMCamel.csproj -c Release`):
;   iscc BIMCamel.iss
; Or use build_installers.ps1 in this folder (also builds the plugin and generates wizard assets).
;
; Output: installer\output\BIMCamel_Setup.exe

#define AppName    "BIMCamel IFC Exporter"
#define AppShort   "BIMCamel"
#define AppVer     "0.1.0"
#define AppGuid    "8A2F1B3C-9D4E-4A5F-8B6C-7E1F2A3B4C5D"
#define AppId      "{{" + AppGuid + "}"
#define AppUrl     "https://www.bimcamel.com"
#define AppUrlPlain "www.bimcamel.com"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=BIMCamel
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
AppContact={#AppUrl}
WizardStyle=modern
WizardImageFile=assets\wizard_image.bmp
WizardSmallImageFile=assets\wizard_small.bmp
SetupIconFile=assets\bimcamel.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={uninstallexe}
DisableProgramGroupPage=yes
DisableDirPage=no
DisableWelcomePage=no
DisableReadyPage=no
Uninstallable=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=BIMCamel_Setup
; Per-user install only: no admin, no UAC, no "all users / just me" prompt. The bundle goes to the
; running user's own AppData (resolved at run time from their profile, not a fixed name), which
; Navisworks auto-loads for that user.
PrivilegesRequired=lowest
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\BIMCamel.bundle
ShowLanguageDialog=no

[Messages]
WelcomeLabel2=This will install [name/ver] for your Windows account.%n%nNo administrator rights are needed.%n%nLearn more at {#AppUrlPlain}
FinishedLabelNoIcons=Setup has finished installing [name].%n%nNavisworks will pick up the plug-in automatically on next launch.

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
; A DLL is bound to the Navisworks API it was compiled against (2024=21.x, 2025=22.x, 2026=23.x) and
; is rejected by other versions (PLUGIN_LOAD_07), so each version gets its own folder + DLL. The DLLs
; are built per-version and staged under installer\stage\<year>\ by build_installers.ps1; a year that
; wasn't built is skipped (skipifsourcedoesntexist). The ribbon XAML and icons are resolved relative
; to the DLL, so each year folder carries its own en-US\ and Resources\ copy.
; ── 2024 ──
Source: "stage\2024\BIMCamel.dll";      DestDir: "{app}\2024";           Flags: ignoreversion skipifsourcedoesntexist; Components: n2024man n2024sim
Source: "..\BIMCamel\BIMCamel.xaml";    DestDir: "{app}\2024\en-US";     Flags: ignoreversion; Components: n2024man n2024sim
Source: "..\BIMCamel\Resources\*.png";  DestDir: "{app}\2024\Resources"; Flags: ignoreversion; Components: n2024man n2024sim
; ── 2025 ──
Source: "stage\2025\BIMCamel.dll";      DestDir: "{app}\2025";           Flags: ignoreversion skipifsourcedoesntexist; Components: n2025man n2025sim
Source: "..\BIMCamel\BIMCamel.xaml";    DestDir: "{app}\2025\en-US";     Flags: ignoreversion; Components: n2025man n2025sim
Source: "..\BIMCamel\Resources\*.png";  DestDir: "{app}\2025\Resources"; Flags: ignoreversion; Components: n2025man n2025sim
; ── 2026 ──
Source: "stage\2026\BIMCamel.dll";      DestDir: "{app}\2026";           Flags: ignoreversion skipifsourcedoesntexist; Components: n2026man n2026sim
Source: "..\BIMCamel\BIMCamel.xaml";    DestDir: "{app}\2026\en-US";     Flags: ignoreversion; Components: n2026man n2026sim
Source: "..\BIMCamel\Resources\*.png";  DestDir: "{app}\2026\Resources"; Flags: ignoreversion; Components: n2026man n2026sim

[UninstallDelete]
; The PackageContents.xml is generated at install time (so it isn't in the install log) and
; Navisworks keeps half-loading a bundle whose manifest lingers. Remove the whole bundle folder
; on uninstall so nothing is left behind.
Type: filesandordirs; Name: "{app}"

[Tasks]
Name: "openweb"; Description: "Visit {#AppUrlPlain} after install"; Flags: unchecked

[Run]
Filename: "{#AppUrl}"; Description: "Visit {#AppUrlPlain}"; Flags: shellexec postinstall skipifsilent nowait runasoriginaluser; Tasks: openweb

[Icons]
; Start-Menu shortcut to the website (per-user programs folder).
Name: "{userprograms}\BIMCamel\Visit {#AppUrlPlain}"; Filename: "{#AppUrl}"

[Code]
const
  LegacyKeyAdminName  = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\BIMCamel IFC Exporter_is1';

var
  PriorPath:    string;
  PriorUninst:  string;
  PriorScope:   string;  { 'HKLM', 'HKCU', 'fs-machine', 'fs-user' }

{ The uninstall registry key Inno creates is "<resolved AppId>_is1". The resolved AppId is the GUID
  in single braces, e.g. {8A2F...}. (Building it here from the bare GUID avoids the classic ISPP
  pitfall where {#AppId} carries the doubled "{{" brace into a Pascal string literal and the lookup
  never matches.) }
function UninstallKeyGuid(): string;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppGuid}' + '}_is1';
end;

function TryRead(RootKey: Integer; SubKey: string; var Path, Cmd: string): Boolean;
begin
  Path := '';
  Cmd  := '';
  Result := False;
  if RegQueryStringValue(RootKey, SubKey, 'UninstallString', Cmd) then begin
    if not RegQueryStringValue(RootKey, SubKey, 'InstallLocation', Path) then
      Path := '';
    Result := True;
  end;
end;

function ReadAnyKey(RootKey: Integer; var Path, Cmd: string): Boolean;
begin
  Result := TryRead(RootKey, UninstallKeyGuid(), Path, Cmd);
  if not Result then
    Result := TryRead(RootKey, LegacyKeyAdminName, Path, Cmd);
end;

function FindExistingInstall(): Boolean;
var
  Path, Cmd: string;
begin
  Result := False;

  { A leftover machine-wide ("all users") install from older builds registers under HKLM.
    Check it first so we can offer to clear it. }
  if ReadAnyKey(HKLM, Path, Cmd) then begin
    PriorScope := 'HKLM'; PriorPath := Path; PriorUninst := Cmd;
    Result := True;
    Exit;
  end;

  if ReadAnyKey(HKCU, Path, Cmd) then begin
    PriorScope := 'HKCU'; PriorPath := Path; PriorUninst := Cmd;
    Result := True;
    Exit;
  end;

  { File-system fallback: someone copied the bundle in by hand (no registered uninstaller). }
  Path := ExpandConstant('{commonappdata}\Autodesk\ApplicationPlugins\BIMCamel.bundle');
  if DirExists(Path) then begin
    PriorScope := 'fs-machine'; PriorPath := Path; PriorUninst := '';
    Result := True;
    Exit;
  end;

  Path := ExpandConstant('{userappdata}\Autodesk\ApplicationPlugins\BIMCamel.bundle');
  if DirExists(Path) then begin
    PriorScope := 'fs-user'; PriorPath := Path; PriorUninst := '';
    Result := True;
    Exit;
  end;
end;

function SplitUninstallString(const S: string; var ExePath, ExeArgs: string): Boolean;
var
  CloseQuote: Integer;
begin
  Result := False;
  ExePath := ''; ExeArgs := '';
  if S = '' then Exit;

  if (Length(S) > 0) and (S[1] = '"') then begin
    CloseQuote := Pos('"', Copy(S, 2, Length(S) - 1));
    if CloseQuote = 0 then Exit;
    ExePath := Copy(S, 2, CloseQuote - 1);
    ExeArgs := Trim(Copy(S, CloseQuote + 2, Length(S)));
  end else begin
    ExePath := S;
    ExeArgs := '';
  end;
  Result := ExePath <> '';
end;

{ Delete a directory that the current (non-elevated) process can't remove — e.g. a leftover
  machine-wide bundle under ProgramData. Falls back to an elevated rmdir (one UAC prompt). }
function RemoveDirMaybeElevated(const Dir: string): Boolean;
var
  ResultCode: Integer;
begin
  if not DirExists(Dir) then begin
    Result := True;
    Exit;
  end;
  Result := DelTree(Dir, True, True, True);
  if Result then Exit;

  { No write access (machine-wide path) — elevate a rmdir. }
  Result := ShellExec('runas', 'cmd.exe', '/c rmdir /s /q "' + Dir + '"', '',
                      SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and not DirExists(Dir);
end;

function RunPriorUninstaller(): Boolean;
var
  ResultCode: Integer;
  ExePath, ExeArgs: string;
begin
  Result := False;

  if PriorUninst <> '' then begin
    if not SplitUninstallString(PriorUninst, ExePath, ExeArgs) then Exit;
    if ExeArgs = '' then
      ExeArgs := '/SILENT /SUPPRESSMSGBOXES /NORESTART'
    else
      ExeArgs := ExeArgs + ' /SILENT /SUPPRESSMSGBOXES /NORESTART';
    { A machine-wide (HKLM) uninstaller self-elevates via its own manifest, so this prompts for
      UAC when needed. }
    Result := Exec(ExePath, ExeArgs, '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
              and (ResultCode = 0);
    { Older builds had no [UninstallDelete], so the generated PackageContents.xml (and the bundle
      folder) can linger. Sweep it so Navisworks doesn't keep half-loading the plug-in. }
    if (PriorPath <> '') and DirExists(PriorPath) then
      RemoveDirMaybeElevated(PriorPath);
    Exit;
  end;

  { No uninstaller registered — it was a folder copy. Just delete the bundle. }
  if PriorPath <> '' then
    Result := RemoveDirMaybeElevated(PriorPath);
end;

function InitializeSetup(): Boolean;
var
  Msg: string;
  Reply: Integer;
begin
  Result := True;
  if not FindExistingInstall() then Exit;

  Msg :=
    'A previous installation of {#AppName} was found at:' + #13#10 + #13#10 +
    '    ' + PriorPath + #13#10 + #13#10 +
    'Yes    - Uninstall it now and exit' + #13#10 +
    'No     - Continue and upgrade / overwrite' + #13#10 +
    'Cancel - Abort';

  Reply := MsgBox(Msg, mbConfirmation, MB_YESNOCANCEL);

  if Reply = IDYES then begin
    if not RunPriorUninstaller() then
      MsgBox('Uninstall did not complete cleanly. The previous install may still be present.',
             mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Reply = IDCANCEL then begin
    Result := False;
    Exit;
  end;

  { User chose to continue (upgrade). We only install per-user now, so:
      - a leftover machine-wide install (HKLM, or a ProgramData folder copy) would shadow / duplicate
        the per-user bundle — remove it (elevating if needed);
      - a per-user folder copy at our target gets overwritten by Inno; a registered per-user install
        is handled by Inno's AppId-based upgrade. }
  if PriorScope = 'HKLM' then
    RunPriorUninstaller()
  else if PriorScope = 'fs-machine' then
    RemoveDirMaybeElevated(PriorPath)
  else if PriorScope = 'fs-user' then
    DelTree(PriorPath, True, True, True);
end;

procedure InitializeWizard();
var
  ParentFolder: string;
begin
  { If the user's Autodesk ApplicationPlugins folder isn't where we expect, leave the wizard's
    directory page enabled (it already is) but warn the user up front so they can browse to the
    right place. }
  ParentFolder := ExpandConstant('{userappdata}\Autodesk\ApplicationPlugins');
  if not DirExists(ParentFolder) then begin
    MsgBox(
      'The Autodesk ApplicationPlugins folder was not found at:' + #13#10 +
      '    ' + ParentFolder + #13#10 + #13#10 +
      'Navisworks may not have created it yet. On the next page you can browse to your ' +
      'ApplicationPlugins folder (usually under "Autodesk" in your AppData), or accept the ' +
      'default and Setup will create it.',
      mbInformation, MB_OK);
  end;
end;

function StripTrailingSlash(const S: string): string;
begin
  Result := S;
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Result := Copy(Result, 1, Length(Result) - 1);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Target, Parent, LowerTarget: string;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;

  Target := StripTrailingSlash(WizardForm.DirEdit.Text);
  Parent := ExtractFilePath(Target);

  if (Parent <> '') and not DirExists(Parent) then begin
    if not ForceDirectories(Parent) then begin
      MsgBox(
        'The folder you picked is inside a path that does not exist and cannot be created:' + #13#10 +
        '    ' + Parent + #13#10 + #13#10 +
        'Please pick a different folder (use Browse...).',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  LowerTarget := Lowercase(Target);
  if Pos('applicationplugins', LowerTarget) = 0 then begin
    if MsgBox(
        'The folder you picked does not look like a Navisworks ApplicationPlugins folder:' + #13#10 +
        '    ' + Target + #13#10 + #13#10 +
        'Navisworks only auto-loads bundles from an "ApplicationPlugins" folder. Continue anyway?',
        mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

function CompBlock(Desc, Platform, Series, Year: string): string;
begin
  Result :=
    '  <Components Description="' + Desc + '">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="' + Platform + '" SeriesMin="' + Series + '" SeriesMax="' + Series + '" />' + #13#10 +
    '    <ComponentEntry AppType="ManagedPlugin" ModuleName=".\' + Year + '\BIMCamel.dll" />' + #13#10 +
    '  </Components>' + #13#10;
end;

{ A version belongs in the manifest only if its (selected) component was installed AND a DLL for that
  year actually landed — that way a build that only produced, say, the 2025 DLL doesn't advertise a
  2024/2026 plug-in that would fail to load. }
function YearInstalled(const Comp, Year: string): Boolean;
begin
  Result := WizardIsComponentSelected(Comp)
            and FileExists(ExpandConstant('{app}\' + Year + '\BIMCamel.dll'));
end;

procedure WriteManifest();
var
  Xml: string;
begin
  Xml :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<ApplicationPackage SchemaVersion="3.0" Version="' + '{#AppVer}' + '.0" Author="BIMCamel"' + #13#10 +
    '    ProductCode="C3D4E5F6-A7B8-9012-CDEF-234567890123"' + #13#10 +
    '    Name="{#AppName}"' + #13#10 +
    '    Description="Fast, free Navisworks to IFC exporter (IFC4 / IFC2x3) - {#AppUrlPlain}">' + #13#10 +
    '  <CompanyDetails Name="BIMCamel" Url="{#AppUrl}" />' + #13#10;

  if YearInstalled('n2024man', '2024') then Xml := Xml + CompBlock('2024 Manage',   'NAVMAN', 'Nw21', '2024');
  if YearInstalled('n2024sim', '2024') then Xml := Xml + CompBlock('2024 Simulate', 'NAVSIM', 'Nw21', '2024');
  if YearInstalled('n2025man', '2025') then Xml := Xml + CompBlock('2025 Manage',   'NAVMAN', 'Nw22', '2025');
  if YearInstalled('n2025sim', '2025') then Xml := Xml + CompBlock('2025 Simulate', 'NAVSIM', 'Nw22', '2025');
  if YearInstalled('n2026man', '2026') then Xml := Xml + CompBlock('2026 Manage',   'NAVMAN', 'Nw23', '2026');
  if YearInstalled('n2026sim', '2026') then Xml := Xml + CompBlock('2026 Simulate', 'NAVSIM', 'Nw23', '2026');

  Xml := Xml + '</ApplicationPackage>' + #13#10;
  SaveStringToFile(ExpandConstant('{app}\PackageContents.xml'), Xml, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteManifest();
end;
