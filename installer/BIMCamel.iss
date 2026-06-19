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
#define AppVer     "0.2.1"
#define AppGuid    "8A2F1B3C-9D4E-4A5F-8B6C-7E1F2A3B4C5D"
#define AppId      "{{" + AppGuid + "}"
#define AppUrl     "https://www.bimcamel.com"
#define AppUrlPlain "www.bimcamel.com"
#define DllSrc     "..\BIMCamel\bin\Release\net48\BIMCamel.dll"

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
; Always write a detailed log of every run. Used by the diagnostics below: if Setup detects a
; problem that would stop the plug-in from appearing, it copies this log somewhere the user can
; find it and share it with support.
SetupLogging=yes
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
Source: "{#DllSrc}";                    DestDir: "{app}\Contents";           Flags: ignoreversion
Source: "..\BIMCamel\BIMCamel.xaml";    DestDir: "{app}\Contents\en-US";     Flags: ignoreversion
Source: "..\BIMCamel\Resources\*.png";  DestDir: "{app}\Contents\Resources"; Flags: ignoreversion

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

  { Navisworks detection / install diagnostics }
  NavDetected:  Boolean;  { detection has run }
  NavCount:     Integer;  { number of supported Navisworks installs found }
  NavComps:     string;   { CSV of detected component ids, e.g. 'n2024man,n2025man' }
  NavSummary:   string;   { human-readable list of what was found }
  CompsPreset:  Boolean;  { components page was pre-ticked from detection }

{ The uninstall registry key Inno creates is "<resolved AppId>_is1", where the resolved AppId is the
  GUID wrapped in single braces. We rebuild that string from the bare GUID here to avoid the classic
  ISPP pitfall where the AppId macro carries a doubled brace into a Pascal string literal so the
  lookup never matches. }
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

{ ── Navisworks detection ────────────────────────────────────────────────────────────────────
  Find which supported Navisworks (Manage / Simulate, 2024-2026) are actually installed and where.
  Series numbers: 2024 = 21, 2025 = 22, 2026 = 23 (these are the Nw21/Nw22/Nw23 in the manifest). }

{ Return the install folder of a given Navisworks, or '' if it isn't installed. Tries the registry
  InstallDir first (handles installs on a non-default drive), then the default Program Files path.
  In both cases the folder only counts if it actually contains Roamer.exe (the Navisworks host). }
function NavDir(const Flavour, Year, Ver: string): string;
var
  Dir: string;
begin
  Result := '';

  if RegQueryStringValue(HKLM64, 'SOFTWARE\Autodesk\Navisworks ' + Flavour + ' x64\' + Ver + '.0',
                         'InstallDir', Dir)
     or RegQueryStringValue(HKLM64, 'SOFTWARE\Autodesk\Navisworks ' + Flavour + '\' + Ver + '.0',
                            'InstallDir', Dir) then begin
    Dir := RemoveBackslash(Dir);
    if (Dir <> '') and FileExists(Dir + '\Roamer.exe') then begin
      Result := Dir;
      Exit;
    end;
  end;

  Dir := ExpandConstant('{autopf}') + '\Autodesk\Navisworks ' + Flavour + ' ' + Year;
  if FileExists(Dir + '\Roamer.exe') then
    Result := Dir;
end;

procedure CheckOneNav(const Flavour, Year, Ver, CompId: string);
var
  Dir: string;
begin
  Dir := NavDir(Flavour, Year, Ver);
  if Dir <> '' then begin
    NavCount := NavCount + 1;
    if NavComps <> '' then NavComps := NavComps + ',';
    NavComps := NavComps + CompId;
    NavSummary := NavSummary + '    Navisworks ' + Flavour + ' ' + Year + '   ->   ' + Dir + #13#10;
    Log('BIMCamel: detected Navisworks ' + Flavour + ' ' + Year + ' at ' + Dir);
  end else
    Log('BIMCamel: Navisworks ' + Flavour + ' ' + Year + ' not found');
end;

procedure DetectNavisworks();
begin
  if NavDetected then Exit;
  NavDetected := True;
  NavCount := 0; NavComps := ''; NavSummary := '';

  CheckOneNav('Manage',   '2024', '21', 'n2024man');
  CheckOneNav('Simulate', '2024', '21', 'n2024sim');
  CheckOneNav('Manage',   '2025', '22', 'n2025man');
  CheckOneNav('Simulate', '2025', '22', 'n2025sim');
  CheckOneNav('Manage',   '2026', '23', 'n2026man');
  CheckOneNav('Simulate', '2026', '23', 'n2026sim');

  Log('BIMCamel: Navisworks detection complete. count=' + IntToStr(NavCount) +
      ' comps=[' + NavComps + ']');
end;

{ True if at least one ticked component matches a Navisworks we actually found. (Component ids are
  all distinct and none is a substring of another, so a Pos() membership test is safe.) }
function SelectedMatchesDetected(): Boolean;
begin
  Result :=
    (WizardIsComponentSelected('n2024man') and (Pos('n2024man', NavComps) > 0)) or
    (WizardIsComponentSelected('n2024sim') and (Pos('n2024sim', NavComps) > 0)) or
    (WizardIsComponentSelected('n2025man') and (Pos('n2025man', NavComps) > 0)) or
    (WizardIsComponentSelected('n2025sim') and (Pos('n2025sim', NavComps) > 0)) or
    (WizardIsComponentSelected('n2026man') and (Pos('n2026man', NavComps) > 0)) or
    (WizardIsComponentSelected('n2026sim') and (Pos('n2026sim', NavComps) > 0));
end;

{ Copy the full Setup log somewhere the user can easily find and email to us. Returns the path the
  user should share (the Desktop copy, or the original temp log if the copy fails). }
function ShareableLog(): string;
var
  Src, Dst: string;
begin
  Result := '';
  Src := ExpandConstant('{log}');
  if Src = '' then Exit;
  Dst := ExpandConstant('{userdesktop}\BIMCamel_install_log.txt');
  if CopyFile(Src, Dst, False) then
    Result := Dst
  else
    Result := Src;
end;

{ After install, confirm the payload really landed and that the chosen versions match an installed
  Navisworks. If anything is off — including the common "installed fine but won't show because the
  manifest targets a version you don't have" case — surface a clear error plus a shareable log. }
procedure VerifyInstall();
var
  Problem, DllPath, ManifestPath, LogPath: string;
begin
  Problem := '';
  DllPath      := ExpandConstant('{app}\Contents\BIMCamel.dll');
  ManifestPath := ExpandConstant('{app}\PackageContents.xml');

  if not FileExists(DllPath) then
    Problem := Problem + '- The plug-in file was not copied:' + #13#10 + '      ' + DllPath + #13#10;
  if not FileExists(ManifestPath) then
    Problem := Problem + '- The plug-in manifest was not written:' + #13#10 + '      ' + ManifestPath + #13#10;

  if NavCount = 0 then
    Problem := Problem +
      '- No supported Navisworks (2024-2026) was found on this PC, so the plug-in will not appear ' +
      'until a supported Navisworks is installed.' + #13#10
  else if not SelectedMatchesDetected() then
    Problem := Problem +
      '- The Navisworks version(s) selected during setup do not match the Navisworks found on this ' +
      'PC, so Navisworks will not show the plug-in. Re-run Setup and tick the version you have.' + #13#10;

  if Problem = '' then begin
    Log('BIMCamel: install verification passed.');
    Exit;
  end;

  Log('BIMCamel: install verification FAILED:' + #13#10 + Problem);
  LogPath := ShareableLog();
  MsgBox(
    'BIMCamel finished copying its files, but Setup found a problem that may stop the plug-in from ' +
    'appearing in Navisworks:' + #13#10 + #13#10 +
    Problem + #13#10 +
    'A diagnostic log has been saved here:' + #13#10 +
    '    ' + LogPath + #13#10 + #13#10 +
    'Please send that file to us at {#AppUrlPlain} so we can help you get it working.',
    mbError, MB_OK);
end;

procedure InitializeWizard();
var
  ParentFolder: string;
begin
  DetectNavisworks();

  { Fail guard: nothing supported on this machine. Let the user proceed (they may be installing
    ahead of Navisworks, or have it in an unusual place) but make the consequence explicit. }
  if NavCount = 0 then
    MsgBox(
      'Setup could not find Navisworks 2024, 2025 or 2026 (Manage or Simulate) on this computer.' + #13#10 + #13#10 +
      'BIMCamel only runs inside a supported Navisworks. You can continue, but the plug-in will not ' +
      'appear until one is installed.' + #13#10 + #13#10 +
      'If you do have Navisworks in a non-standard location, continue and make sure the matching ' +
      'version is ticked on the components page.',
      mbInformation, MB_OK)
  else
    Log('BIMCamel: ' + IntToStr(NavCount) + ' supported Navisworks install(s) found:' + #13#10 +
        NavSummary);

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

{ Pre-tick exactly the Navisworks versions we detected, so the generated manifest matches what's
  actually installed. A manifest entry for a version the user doesn't have is the usual reason the
  plug-in silently fails to load. The user can still adjust the ticks. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectComponents) and (not CompsPreset) and (NavCount > 0) then begin
    CompsPreset := True;
    WizardSelectComponents('!n2024man,!n2024sim,!n2025man,!n2025sim,!n2026man,!n2026sim');
    WizardSelectComponents(NavComps);
    Log('BIMCamel: pre-selected components from detection: [' + NavComps + ']');
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
    '<ApplicationPackage SchemaVersion="3.0" Version="' + '{#AppVer}' + '.0" Author="BIMCamel"' + #13#10 +
    '    ProductCode="C3D4E5F6-A7B8-9012-CDEF-234567890123"' + #13#10 +
    '    Name="{#AppName}"' + #13#10 +
    '    Description="Fast, free Navisworks to IFC exporter (IFC4 / IFC2x3) - {#AppUrlPlain}">' + #13#10 +
    '  <CompanyDetails Name="BIMCamel" Url="{#AppUrl}" />' + #13#10;

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
  if CurStep = ssPostInstall then begin
    WriteManifest();
    VerifyInstall();
  end;
end;
