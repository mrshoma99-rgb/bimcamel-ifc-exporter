; BIMCamel IFC Exporter — per-user Inno Setup installer
;
; One installer (BIMCamel_Setup.exe), per-user (local) install only:
;   * Installs to the current user's Autodesk ApplicationPlugins folder
;     (%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle) — no admin, no UAC.
;     This is the location Navisworks reliably auto-loads for the logged-in user, and the
;     path is derived from the running user's profile (never a fixed/developer name).
;   * Shows a "Navisworks check" page up front: which supported Navisworks (Manage / Simulate,
;     2024-2026) it found and where, and what it's about to do — no pop-ups, no decisions.
;   * Pre-selects the Navisworks version(s) it detected so the generated manifest matches what's
;     actually installed (a mismatch is the usual reason the plug-in never appears).
;   * Detects a previous BIMCamel install — including a leftover machine-wide ("all users")
;     install from older builds — and upgrades / removes it automatically (elevating once only if
;     a system-wide copy must be deleted). Full uninstall lives in Apps & Features.
;   * Verifies the payload after install; if anything looks wrong (files missing, or the chosen
;     version doesn't match a detected Navisworks) it explains it on the Finished page and saves a
;     shareable log to the Desktop — instead of a cryptic error box.
;   * Registers a proper uninstaller (Apps & Features) that removes the whole bundle, including
;     the generated PackageContents.xml, so nothing is left behind for Navisworks to half-load.
;   * Lets the user change the destination folder on the directory page.
;   * Carries the BIMCamel logo on the wizard and a "Visit www.bimcamel.com" task.
;
; Build (after `dotnet build ..\BIMCamel\BIMCamel.csproj -c Release`):
;   iscc BIMCamel.iss
; Or use build_installers.ps1 in this folder (also builds the plugin and generates wizard assets).
;
; Output: installer\output\BIMCamel_Setup.exe

#define AppName    "BIMCamel IFC Exporter"
#define AppShort   "BIMCamel"
#define AppVer     "0.3.0"
#define AppGuid    "8A2F1B3C-9D4E-4A5F-8B6C-7E1F2A3B4C5D"
#define AppId      "{{" + AppGuid + "}"
#define AppUrl     "https://www.bimcamel.com"
#define AppUrlPlain "www.bimcamel.com"
; Per-year plug-in DLLs are staged here by build_installers.ps1 (staging\<year>\BIMCamel.dll), each
; compiled against that Navisworks release's API. There is no single all-years DLL.
#define StageDir   "staging"

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
; Per-year layout: each Navisworks release gets its own subfolder holding the DLL built against that
; year's API, plus its own en-US ribbon layout and icon Resources (Navisworks resolves both relative
; to the DLL's folder). Files are gated by component so only selected versions are written. The DLL
; uses skipifsourcedoesntexist so the installer still compiles on a build PC that lacks some
; Navisworks versions; VerifyInstall flags any selected year whose DLL didn't make it in.
Source: "{#StageDir}\2024\BIMCamel.dll"; DestDir: "{app}\2024";           Components: n2024man n2024sim; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\BIMCamel\BIMCamel.xaml";     DestDir: "{app}\2024\en-US";     Components: n2024man n2024sim; Flags: ignoreversion
Source: "..\BIMCamel\Resources\*.png";   DestDir: "{app}\2024\Resources"; Components: n2024man n2024sim; Flags: ignoreversion

Source: "{#StageDir}\2025\BIMCamel.dll"; DestDir: "{app}\2025";           Components: n2025man n2025sim; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\BIMCamel\BIMCamel.xaml";     DestDir: "{app}\2025\en-US";     Components: n2025man n2025sim; Flags: ignoreversion
Source: "..\BIMCamel\Resources\*.png";   DestDir: "{app}\2025\Resources"; Components: n2025man n2025sim; Flags: ignoreversion

Source: "{#StageDir}\2026\BIMCamel.dll"; DestDir: "{app}\2026";           Components: n2026man n2026sim; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\BIMCamel\BIMCamel.xaml";     DestDir: "{app}\2026\en-US";     Components: n2026man n2026sim; Flags: ignoreversion
Source: "..\BIMCamel\Resources\*.png";   DestDir: "{app}\2026\Resources"; Components: n2026man n2026sim; Flags: ignoreversion

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

  { Custom UI / state (replaces the old decision pop-ups) }
  EnvPage:        TWizardPage;  { "Navisworks check" page shown after Welcome }
  HasPrior:       Boolean;      { a previous BIMCamel install was found }
  InstallProblem: string;       { non-empty => show a warning on the Finished page }
  ProblemLogPath: string;       { where the shareable diagnostic log was saved }

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

{ Prior-install cleanup, done automatically just before files are copied (no pop-up, no decision).
  We only install per-user now, so a leftover machine-wide install (HKLM, or a ProgramData folder
  copy) would shadow / duplicate the per-user bundle — remove it (elevating once if needed). A
  per-user folder copy at our target is overwritten by Inno, and a registered per-user install is
  upgraded in place by Inno via the shared AppId, so neither needs handling here. }
procedure CleanupPriorInstall();
begin
  if PriorScope = 'HKLM' then
    RunPriorUninstaller()
  else if PriorScope = 'fs-machine' then
    RemoveDirMaybeElevated(PriorPath)
  else if PriorScope = 'fs-user' then
    DelTree(PriorPath, True, True, True);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldContents: string;
begin
  Result := '';
  if HasPrior then begin
    Log('BIMCamel: cleaning up prior install (scope=' + PriorScope + ') before installing.');
    CleanupPriorInstall();
  end;

  { Up to 0.3.0 the bundle shipped a single Contents\BIMCamel.dll for all years (the cause of the
    "plug-in loads but no ribbon tab" bug). A registered per-user upgrade is done in place by Inno,
    so that stale folder would linger next to the new per-year folders. Remove it. }
  OldContents := ExpandConstant('{app}\Contents');
  if DirExists(OldContents) then begin
    Log('BIMCamel: removing legacy single-DLL Contents\ folder from a prior install.');
    DelTree(OldContents, True, True, True);
  end;
end;

{ ── Navisworks detection ────────────────────────────────────────────────────────────────────
  Find which supported Navisworks (Manage / Simulate, 2024-2026) are actually installed and where.
  Series numbers: 2024 = 21, 2025 = 22, 2026 = 23 (these are the Nw21/Nw22/Nw23 in the manifest). }

{ The 64-bit "Program Files" folder, resolved reliably even though Setup is a 32-bit, per-user
  (non-admin) process. We must NOT use the autopf / pf constants here: in non-admin install mode
  autopf maps to the per-user LocalAppData\Programs folder, and a 32-bit process sees ProgramFiles
  as the (x86) folder - Navisworks (64-bit) lives in the real C:\Program Files. The ProgramW6432
  environment variable always points there. }
function ProgramFiles64(): string;
begin
  Result := GetEnv('ProgramW6432');
  if Result = '' then
    Result := ExpandConstant('{commonpf64}');
end;

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

  Dir := ProgramFiles64() + '\Autodesk\Navisworks ' + Flavour + ' ' + Year;
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

{ True if the per-year plug-in DLL landed for the given Navisworks year. }
function YearDllExists(const Year: string): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\' + Year + '\BIMCamel.dll'));
end;

{ Space-separated list of selected Navisworks years whose DLL is missing after install — i.e. years
  the user picked that weren't included in this installer build (the build PC lacked that Navisworks,
  so build_installers.ps1 couldn't compile that year's DLL against its API). }
function MissingSelectedYears(): string;
var
  M: string;
begin
  M := '';
  if (WizardIsComponentSelected('n2024man') or WizardIsComponentSelected('n2024sim')) and not YearDllExists('2024') then M := M + '2024 ';
  if (WizardIsComponentSelected('n2025man') or WizardIsComponentSelected('n2025sim')) and not YearDllExists('2025') then M := M + '2025 ';
  if (WizardIsComponentSelected('n2026man') or WizardIsComponentSelected('n2026sim')) and not YearDllExists('2026') then M := M + '2026 ';
  Result := Trim(M);
end;

{ After install, confirm the payload really landed and that the chosen versions match an installed
  Navisworks. If anything is off — including the common "installed fine but won't show because the
  manifest targets a version you don't have" case — surface a clear error plus a shareable log. }
procedure VerifyInstall();
var
  Problem, ManifestPath, MissingYears: string;
begin
  Problem := '';
  ManifestPath := ExpandConstant('{app}\PackageContents.xml');

  MissingYears := MissingSelectedYears();
  if MissingYears <> '' then
    Problem := Problem +
      '- The plug-in files for these Navisworks year(s) were not included in this installer: ' +
      MissingYears + '. (The PC that built this installer did not have those Navisworks versions, so ' +
      'their DLLs could not be compiled.)' + #13#10;
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

  InstallProblem := Problem;

  if Problem = '' then begin
    Log('BIMCamel: install verification passed.');
    Exit;
  end;

  { Don't pop an error box — record the problem and the log path so the Finished page can show a
    clear, readable message the user can act on. }
  Log('BIMCamel: install verification FAILED:' + #13#10 + Problem);
  ProblemLogPath := ShareableLog();
end;

{ Build the "Navisworks check" page (after Welcome). It replaces the old pop-up warnings: it simply
  shows what Setup detected and what it will do — the user doesn't have to decide anything. }
procedure BuildEnvironmentPage();
var
  Heading, Warn, PriorLbl: TNewStaticText;
  Memo: TNewMemo;
  Y: Integer;
begin
  EnvPage := CreateCustomPage(wpWelcome, 'Navisworks check',
    'Setup checked this computer for supported Navisworks installations.');

  Heading := TNewStaticText.Create(EnvPage);
  Heading.Parent := EnvPage.Surface;
  Heading.Left := 0;
  Heading.Top := 0;
  Heading.AutoSize := True;
  if NavCount > 0 then
    Heading.Caption := 'Supported Navisworks found on this PC:'
  else
    Heading.Caption := 'No supported Navisworks (2024-2026) was found on this PC.';

  Memo := TNewMemo.Create(EnvPage);
  Memo.Parent := EnvPage.Surface;
  Memo.Left := 0;
  Memo.Top := ScaleY(22);
  Memo.Width := EnvPage.SurfaceWidth;
  Memo.Height := ScaleY(92);
  Memo.ReadOnly := True;
  Memo.ScrollBars := ssVertical;
  if NavCount > 0 then
    Memo.Text := NavSummary + #13#10 +
      'The matching version(s) are pre-selected on the next page, so the plug-in loads ' +
      'into the Navisworks you actually have.'
  else
    Memo.Text :=
      'BIMCamel only runs inside Navisworks Manage or Simulate (2024, 2025 or 2026).' + #13#10 + #13#10 +
      'You can still install now - the plug-in will appear automatically the next time you start a ' +
      'supported Navisworks. Tip: installing Navisworks first, then running this installer, lets ' +
      'Setup pre-select the right version for you.';

  Y := Memo.Top + Memo.Height + ScaleY(14);

  if NavCount = 0 then begin
    Warn := TNewStaticText.Create(EnvPage);
    Warn.Parent := EnvPage.Surface;
    Warn.Left := 0;
    Warn.Top := Y;
    Warn.AutoSize := True;
    Warn.Font.Style := [fsBold];
    Warn.Caption := 'No Navisworks detected - the plug-in will stay hidden until one is installed.';
    Y := Warn.Top + ScaleY(24);
  end;

  if HasPrior then begin
    PriorLbl := TNewStaticText.Create(EnvPage);
    PriorLbl.Parent := EnvPage.Surface;
    PriorLbl.Left := 0;
    PriorLbl.Top := Y;
    PriorLbl.Width := EnvPage.SurfaceWidth;
    PriorLbl.AutoSize := False;
    PriorLbl.Height := ScaleY(40);
    PriorLbl.WordWrap := True;
    if (PriorScope = 'HKLM') or (PriorScope = 'fs-machine') then
      PriorLbl.Caption := 'An existing system-wide BIMCamel install was found. Setup will remove it ' +
        'and install this version for your account. Windows may ask for permission once.'
    else
      PriorLbl.Caption := 'An existing BIMCamel install was found and will be updated to this version.';
  end;
end;

procedure InitializeWizard();
begin
  DetectNavisworks();
  HasPrior := FindExistingInstall();
  if HasPrior then
    Log('BIMCamel: prior install found. scope=' + PriorScope + ' path=' + PriorPath)
  else
    Log('BIMCamel: no prior install found.');
  if NavCount > 0 then
    Log('BIMCamel: ' + IntToStr(NavCount) + ' supported Navisworks install(s) found:' + #13#10 + NavSummary);
  BuildEnvironmentPage();
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

  { If post-install verification spotted a problem, replace the cheery Finished text with a clear,
    actionable message (and the log path) right on the page - no error pop-up. }
  if (CurPageID = wpFinished) and (InstallProblem <> '') then
    WizardForm.FinishedLabel.Caption :=
      'BIMCamel was installed, but Setup found something that may stop the plug-in from appearing in ' +
      'Navisworks:' + #13#10 + #13#10 + InstallProblem + #13#10 +
      'A diagnostic log was saved to:' + #13#10 + '    ' + ProblemLogPath + #13#10 + #13#10 +
      'Please send that file to us at {#AppUrlPlain} and we will help you get it working.';
end;

function StripTrailingSlash(const S: string): string;
begin
  Result := S;
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Result := Copy(Result, 1, Length(Result) - 1);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Target, Parent: string;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;

  Target := StripTrailingSlash(WizardForm.DirEdit.Text);
  Parent := ExtractFilePath(Target);

  { Only a genuine error blocks here: the chosen path's parent can't be created. (A non-standard
    folder no longer triggers a confirm dialog - if the location is wrong, the Finished-page check
    will tell the user the plug-in won't show.) }
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

  if Pos('applicationplugins', Lowercase(Target)) = 0 then
    Log('BIMCamel: chosen target is not under an ApplicationPlugins folder: ' + Target);
end;

function CompBlock(Desc, Platform, Series, Year: string): string;
begin
  Result :=
    '  <Components Description="' + Desc + '">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="' + Platform + '" SeriesMin="' + Series + '" SeriesMax="' + Series + '" />' + #13#10 +
    '    <ComponentEntry AppType="ManagedPlugin" ModuleName=".\' + Year + '\BIMCamel.dll" />' + #13#10 +
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

  if WizardIsComponentSelected('n2024man') then Xml := Xml + CompBlock('2024 Manage',   'NAVMAN', 'Nw21', '2024');
  if WizardIsComponentSelected('n2024sim') then Xml := Xml + CompBlock('2024 Simulate', 'NAVSIM', 'Nw21', '2024');
  if WizardIsComponentSelected('n2025man') then Xml := Xml + CompBlock('2025 Manage',   'NAVMAN', 'Nw22', '2025');
  if WizardIsComponentSelected('n2025sim') then Xml := Xml + CompBlock('2025 Simulate', 'NAVSIM', 'Nw22', '2025');
  if WizardIsComponentSelected('n2026man') then Xml := Xml + CompBlock('2026 Manage',   'NAVMAN', 'Nw23', '2026');
  if WizardIsComponentSelected('n2026sim') then Xml := Xml + CompBlock('2026 Simulate', 'NAVSIM', 'Nw23', '2026');

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
