[Setup]
AppName=GamePinned
#define MyAppVersion "2.0.5"
#define MyAppId "{{2E7B7C8C-3C2A-4A3C-AE92-5B7D2A6E1F3B}}"
AppVersion={#MyAppVersion}
AppId={#MyAppId}
AppPublisher=GamePinned
AppPublisherURL=https://gamepinned.com
AppSupportURL=https://gamepinned.com/support
AppUpdatesURL=https://api.example.com/version
DefaultDirName={autopf}\GamePinned
DefaultGroupName=GamePinned
UninstallDisplayIcon={app}\GamePinned.exe
LicenseFile=PrivacyPolicy.txt
UsePreviousAppDir=yes
DirExistsWarning=no
SetupIconFile=GamePinnedGUI\icon.ico
WizardStyle=modern
DisableWelcomePage=no
WizardImageFile=installer\wizard-banner.png
WizardSmallImageFile=installer\wizard-small.png
WizardImageStretch=yes
Compression=lzma2
SolidCompression=yes
OutputDir=installer
OutputBaseFilename=GamePinned
PrivilegesRequired=admin
; Prevent installation on 32-bit Windows — the service requires 64-bit .NET and icacls features
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
; Minimum Windows 10 1809 — required for .NET 10 and the Named Pipe isolation features we rely on
MinVersion=10.0.17763
; Write a setup log to %TEMP% for diagnosing failed installs on customer PCs
SetupLogging=yes
; Gracefully close running processes before overwriting files.
; In interactive mode this shows a "please close these apps" dialog.
; In /VERYSILENT mode (auto-update) it force-closes them silently.
; Our CurStepChanged(ssInstall) handler below is kept as a belt-and-suspenders fallback.
CloseApplications=yes
CloseApplicationsFilter=*.exe
; Do NOT restart closed apps automatically — CurStepChanged(ssDone) handles GUI relaunch.
RestartApplications=no
; Populate the installer EXE's Version/Details tab in Windows Explorer
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=GamePinned
VersionInfoDescription=GamePinned Installer
VersionInfoProductName=GamePinned
VersionInfoCopyright=Copyright (C) 2026 GamePinned

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Pre-create the data directory so it exists even if the service fails to start on first run.
; uninsneveruninstall: our CurUninstallStepChanged prompt below handles deletion — don't let Inno touch it.
Name: "{commonappdata}\GamePinned"; Flags: uninsneveruninstall
Name: "{commonappdata}\GamePinned\logs"; Flags: uninsneveruninstall
Name: "{commonappdata}\GamePinned\backups"; Flags: uninsneveruninstall

[Files]
Source: "publish\gui\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\service\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GamePinned"; Filename: "{app}\GamePinned.exe"
Name: "{autodesktop}\GamePinned"; Filename: "{app}\GamePinned.exe"; Tasks: desktopicon

[Run]
; Install and start the background service automatically.
Filename: "{app}\GamePinnedService.exe"; Parameters: "--install-service"; Flags: runhidden waituntilterminated; StatusMsg: "Installing GamePinned Service..."
Filename: "sc.exe"; Parameters: "start GamePinned"; Flags: runhidden; StatusMsg: "Starting GamePinned Service..."
; Launch the GUI.
Filename: "{app}\GamePinned.exe"; Description: "Launch GamePinned Control Panel"; Flags: nowait postinstall runascurrentuser
; Open docs page in browser (checked by default, user can uncheck).
Filename: "https://gamepinned.com/docs"; Description: "Open docs"; Flags: nowait postinstall shellexec runascurrentuser

[UninstallRun]
; Stop and uninstall the background service before removing files.
Filename: "taskkill.exe"; Parameters: "/F /IM GamePinned.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillGUI"
Filename: "sc.exe"; Parameters: "stop GamePinned"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{app}\GamePinnedService.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallService"

[Code]
function TryGetInstalledVersion(var InstalledVersion: string): Boolean;
var
  Key: string;
begin
  InstalledVersion := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + ExpandConstant('{#MyAppId}') + '_is1';

  if RegQueryStringValue(HKLM, Key, 'DisplayVersion', InstalledVersion) then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKCU, Key, 'DisplayVersion', InstalledVersion) then
  begin
    Result := True;
    Exit;
  end;

  Result := False;
end;

function TryGetLegacyInstalledVersion(var InstalledVersion: string): Boolean;
var
  ExistingExe: string;
begin
  InstalledVersion := '';
  ExistingExe := ExpandConstant('{autopf}\GamePinned\GUI\GamePinned.exe');

  if FileExists(ExistingExe) then
  begin
    if GetVersionNumbersString(ExistingExe, InstalledVersion) then
    begin
      Result := True;
      Exit;
    end;

    InstalledVersion := '0.0.0';
    Result := True;
    Exit;
  end;

  Result := False;
end;

function NextIntPart(const S: string; var P: Integer): Integer;
var
  StartP, DotP: Integer;
  Part: string;
begin
  while (P <= Length(S)) and (S[P] = ' ') do
    P := P + 1;

  StartP := P;
  DotP := 0;
  while (P <= Length(S)) do
  begin
    if S[P] = '.' then
    begin
      DotP := P;
      Break;
    end;
    P := P + 1;
  end;

  if DotP = 0 then
    Part := Copy(S, StartP, Length(S) - StartP + 1)
  else
    Part := Copy(S, StartP, DotP - StartP);

  if DotP = 0 then
    P := Length(S) + 1
  else
    P := DotP + 1;

  Result := StrToIntDef(Part, 0);
end;

function CompareVersions(const A, B: string): Integer;
var
  PA, PB: Integer;
  IA, IB: Integer;
begin
  PA := 1;
  PB := 1;

  while (PA <= Length(A)) or (PB <= Length(B)) do
  begin
    if PA <= Length(A) then IA := NextIntPart(A, PA) else IA := 0;
    if PB <= Length(B) then IB := NextIntPart(B, PB) else IB := 0;

    if IA < IB then begin Result := -1; Exit; end;
    if IA > IB then begin Result := 1; Exit; end;
  end;

  Result := 0;
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: string;
  InstallerVersion: string;
  Cmp: Integer;
begin
  InstallerVersion := ExpandConstant('{#MyAppVersion}');

  if (TryGetInstalledVersion(InstalledVersion)) or (TryGetLegacyInstalledVersion(InstalledVersion)) then
  begin
    Cmp := CompareVersions(InstalledVersion, InstallerVersion);

    // Only block if the installed version is strictly NEWER (downgrade attempt).
    // Same version reinstall is allowed — it acts as a repair and is standard industry behavior.
    if Cmp > 0 then
    begin
      MsgBox('A newer version of GamePinned (' + InstalledVersion + ') is already installed.' + #13#10 +
             'Downgrading is not supported.', mbInformation, MB_OK);
      Result := False;
      Exit;
    end
    else if Cmp < 0 then
    begin
      // In silent mode (auto-update via TrySilentUpdate), skip the confirmation dialog.
      // MsgBox in /VERYSILENT mode returns IDOK (1), not IDYES (6), so the check would always
      // abort the install — causing an infinite update loop where the binary is never replaced.
      if WizardSilent then
      begin
        Result := True;
        Exit;
      end;
      if MsgBox('GamePinned is already installed (version ' + InstalledVersion + ').' + #13#10 +
                'Do you want to update to version ' + InstallerVersion + '?', mbConfirmation, MB_YESNO) = IDYES then
      begin
        Result := True;
        Exit;
      end
      else
      begin
        Result := False;
        Exit;
      end;
    end;
    // Cmp = 0: same version — fall through and allow reinstall (repair)
  end;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Kill GUI first so it releases any file handles
    Exec('taskkill.exe', '/F /IM GamePinned.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Graceful stop — asks the service to run its OnStop cleanup (removes deny rules on new versions)
    Exec('sc.exe', 'stop GamePinned', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Wait up to 10 seconds for graceful stop — covers most cases for new versions.
    // Old versions (1.0.0, 1.0.1): OnStop calls RemoveProtection() with icacls /T across all
    // protected game folders, which can take minutes on large game libraries (20+ games, HDD).
    // The force-kill below is the safety net for those cases.
    Sleep(10000);

    // Force-kill the service process so the exe file lock is released before we overwrite it.
    // This is required when upgrading from old versions whose OnStop takes longer than the wait
    // above. If the process already exited gracefully, taskkill exits harmlessly with an error code.
    // The deny-rule cleanup that OnStop didn't finish is handled by --uninstall-service below.
    Exec('taskkill.exe', '/F /IM GamePinnedService.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);

    // Remove old subfolder layout (pre-v2.0.4). Files are now installed directly into {app}\.
    // DelTree on a nonexistent path is a no-op, so this is safe on fresh installs.
    DelTree(ExpandConstant('{app}\GUI'), True, True, True);
    DelTree(ExpandConstant('{app}\Service'), True, True, True);

    // Wipe logs on every upgrade — old plain-text debug logs (pre-encryption builds) would otherwise
    // sit on disk readable by anyone. config.json and NVIDIA profiles are preserved.
    DelTree(ExpandConstant('{commonappdata}\GamePinned\logs'), True, True, True);
  end;

  if CurStep = ssDone then
  begin
    // When running silently (auto-update from background checker), manually relaunch the GUI.
    // postinstall [Run] entries are skipped in silent mode, so we handle it here.
    if WizardSilent then
      ShellExec('open', ExpandConstant('{app}\GamePinned.exe'), '', '', SW_SHOW, ewNoWait, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Force kill GUI before uninstallation starts (to release file locks).
    // The service and its deny-rule cleanup are handled by [UninstallRun] above.
    Exec('taskkill.exe', '/F /IM GamePinned.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the "Launch with Windows" registry entries written by the service's ToggleStartup command.
    // Inno Setup only cleans up the uninstall key it creates itself — these are orphaned without this.
    RegDeleteValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GamePinned');
    RegDeleteValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'GamePinned');
    RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'GamePinned');

    // Ask whether to keep config.json for future reinstallation.
    // Yes = keep only config.json (license key, protected games, blocked users, password).
    // No = delete everything.
    // Logs, backups, NVIDIA profiles, and cache are always deleted regardless.
    if MsgBox(
      'Keep your settings for a future reinstallation?' + #13#10 + #13#10 +
      'Your license key, protected games list, blocked users, and admin password will be preserved.' + #13#10 + #13#10 +
      'Logs, backups, and all other data will be removed either way.',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      // Keep only config.json — delete everything else
      DelTree(ExpandConstant('{commonappdata}\GamePinned\logs'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\GamePinned\backups'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\GamePinned\nvidia-config'), True, True, True);
      DeleteFile(ExpandConstant('{commonappdata}\GamePinned\cache.json'));
      DeleteFile(ExpandConstant('{commonappdata}\GamePinned\update.exe'));
      DeleteFile(ExpandConstant('{commonappdata}\GamePinned\autoupdated.flag'));
    end
    else
    begin
      // Remove everything
      DelTree(ExpandConstant('{commonappdata}\GamePinned'), True, True, True);
    end;
  end;
end;