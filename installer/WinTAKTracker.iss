; WinTAKTracker one-click installer (Inno Setup 6)
; Builds WinTAKTracker-Setup.exe — service + tray under Program Files, single UAC prompt.
;
; Expected layout (from release CI / local publish):
;   publish\WinTAKTracker.exe
;   publish\service\*   (WinTAKTracker.Service.exe + deps)
;
; Compile:
;   ISCC.exe /DMyAppVersion=0.1.0 installer\WinTAKTracker.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef MyAppVersionInfo
  #define MyAppVersionInfo "0.0.0.0"
#endif

#define MyAppName "WinTAKTracker"
#define MyAppPublisher "CopIX LLC"
#define MyAppURL "https://github.com/CopIXus/WinTAKTracker"
#define MyAppExeName "WinTAKTracker.exe"
#define MyServiceExeName "WinTAKTracker.Service.exe"
#define MyServiceName "WinTAKTracker"

[Setup]
AppId={{A7E3C2B1-4F5D-4A8E-9C1B-6D2E8F0A3B47}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=WinTAKTracker-Setup
SetupIconFile=..\src\WinTAKTracker\Assets\WinTAKTrackerLogo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersionInfo}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nIt installs the always-on Windows Service and the tray companion in one step (one administrator approval).%n%nIt is recommended that you close WinTAKTracker before continuing.
FinishedLabelNoIcons=Setup has finished installing [name] on your computer.%n%nThe Windows Service is registered for automatic start. The tray app can control tracking without keeping a second tracker running.
FinishedHeadingLabel=Installation complete

[Tasks]
Name: "desktopicon"; Description: "Create a &Desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "migrateconfig"; Description: "Migrate portable config from %LocalAppData%\WinTAKTracker (if present)"; GroupDescription: "Configuration:"; Flags: checkedonce; Check: HasPortableUserConfig

[Files]
; Tray (self-contained single-file)
Source: "..\publish\WinTAKTracker.exe"; DestDir: "{app}"; Flags: ignoreversion
; Optional FFmpeg essentials (fetched by CI / scripts/fetch-ffmpeg.ps1) for video streaming
Source: "..\publish\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\publish\THIRD_PARTY_FFMPEG.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Service binaries (self-contained publish folder)
Source: "..\publish\service\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Manual / advanced reinstall helper (optional)
Source: "..\scripts\install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "WinTAKTracker tray controller"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "WinTAKTracker tray controller"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch WinTAKTracker now"; Flags: nowait postinstall skipifsilent

[Code]
const
  ServiceName = '{#MyServiceName}';
  ServiceDisplayName = '{#MyAppName}';
  ServiceDescription =
    'Always-on TAK PLI tracker (NMEA/Mesh/TAK). Tray UI is a companion controller. Named pipe: WinTAKTracker.Control';

function HasPortableUserConfig: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\WinTAKTracker\config.json'));
end;

function ScExec(const Args: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := -1
  else
    Result := ResultCode;
end;

function ServiceExists: Boolean;
begin
  { sc query returns 0 when the service exists }
  Result := ScExec('query "' + ServiceName + '"') = 0;
end;

procedure StopServiceIfPresent;
var
  ResultCode: Integer;
begin
  if ServiceExists then
  begin
    Log('Stopping service ' + ServiceName);
    ScExec('stop "' + ServiceName + '"');
    { Give SCM a moment to release the binary }
    Sleep(1500);
    { Best-effort wait loop }
    ResultCode := 0;
    while (ResultCode < 20) and ServiceExists do
    begin
      if ScExec('query "' + ServiceName + '"') <> 0 then
        Break;
      { If STATE is STOPPED, query still succeeds — try stop again then continue }
      ScExec('stop "' + ServiceName + '"');
      Sleep(500);
      Inc(ResultCode);
      { Break after a few attempts even if still running; delete will follow }
      if ResultCode >= 6 then
        Break;
    end;
  end;
end;

procedure DeleteServiceIfPresent;
begin
  if ServiceExists then
  begin
    Log('Deleting service ' + ServiceName);
    StopServiceIfPresent;
    ScExec('delete "' + ServiceName + '"');
    Sleep(1500);
  end;
end;

{ Machine store ACL:
  Root/logs/updates: SYSTEM+Admins Full; Authenticated Users Modify
  secrets/certs: SYSTEM+Admins Full only (tray mutates secrets via service IPC when possible) }
procedure EnsureMachineStoreAcl;
var
  MachineRoot, SecretsDir, CertsDir, LogsDir, UpdatesDir: String;
  ResultCode: Integer;
begin
  MachineRoot := ExpandConstant('{commonappdata}\WinTAKTracker');
  SecretsDir := MachineRoot + '\secrets';
  CertsDir := MachineRoot + '\certs';
  LogsDir := MachineRoot + '\logs';
  UpdatesDir := MachineRoot + '\updates';
  ForceDirectories(MachineRoot);
  ForceDirectories(SecretsDir);
  ForceDirectories(CertsDir);
  ForceDirectories(LogsDir);
  ForceDirectories(UpdatesDir);

  Log('Setting hardened ACL on ' + MachineRoot);
  { Reset then grant: SYSTEM + Admins Full; Authenticated Users Modify }
  { S-1-5-18 = SYSTEM; S-1-5-32-544 = Administrators; S-1-5-11 = Authenticated Users }
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + MachineRoot + '" /inheritance:r', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + MachineRoot + '" /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-11:(OI)(CI)M"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + LogsDir + '" /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-11:(OI)(CI)M"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + UpdatesDir + '" /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-11:(OI)(CI)M"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { secrets + certs: SYSTEM + Admins only }
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + SecretsDir + '" /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + CertsDir + '" /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    Log('icacls exit ' + IntToStr(ResultCode))
  else
    Log('Machine store ACL OK (secrets/certs restricted)');
end;

procedure CreateAndStartService;
var
  BinPath: String;
  ResultCode: Integer;
begin
  BinPath := ExpandConstant('{app}\{#MyServiceExeName}');
  if not FileExists(BinPath) then
  begin
    MsgBox('Service binary not found:' + #13#10 + BinPath, mbError, MB_OK);
    Exit;
  end;

  EnsureMachineStoreAcl;
  DeleteServiceIfPresent;

  Log('Creating service ' + ServiceName);
  ResultCode := ScExec(
    'create "' + ServiceName + '" binPath= "' + BinPath + '" start= auto DisplayName= "' + ServiceDisplayName + '"');
  if ResultCode <> 0 then
  begin
    MsgBox('Could not create the WinTAKTracker Windows Service (sc create exit ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
    Exit;
  end;

  ScExec('description "' + ServiceName + '" "' + ServiceDescription + '"');
  ScExec('failure "' + ServiceName + '" reset= 86400 actions= restart/5000/restart/10000/restart/30000');

  Log('Starting service ' + ServiceName);
  ResultCode := ScExec('start "' + ServiceName + '"');
  if ResultCode <> 0 then
    MsgBox('Service was installed but did not start (sc start exit ' + IntToStr(ResultCode) + '). You can start it from services.msc.', mbInformation, MB_OK);
end;

procedure CopyDirRecursive(const SourceDir, DestDir: String);
var
  FindRec: TFindRec;
  SrcPath, DstPath: String;
begin
  if not DirExists(SourceDir) then
    Exit;
  ForceDirectories(DestDir);
  if FindFirst(SourceDir + '\*', FindRec) then
  try
    repeat
      if (FindRec.Name = '.') or (FindRec.Name = '..') then
        Continue;
      SrcPath := SourceDir + '\' + FindRec.Name;
      DstPath := DestDir + '\' + FindRec.Name;
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        CopyDirRecursive(SrcPath, DstPath)
      else
        FileCopy(SrcPath, DstPath, False);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure MigratePortableConfig;
var
  UserRoot, MachineRoot: String;
begin
  UserRoot := ExpandConstant('{localappdata}\WinTAKTracker');
  MachineRoot := ExpandConstant('{commonappdata}\WinTAKTracker');
  if not FileExists(UserRoot + '\config.json') then
    Exit;

  Log('Migrating portable config to ' + MachineRoot);
  ForceDirectories(MachineRoot);
  FileCopy(UserRoot + '\config.json', MachineRoot + '\config.json', False);
  if DirExists(UserRoot + '\certs') then
    CopyDirRecursive(UserRoot + '\certs', MachineRoot + '\certs');

  { Certs + config are copied here. The tray app (running as your user) re-protects DPAPI
    secrets into LocalMachine on first launch so the service can connect without re-enroll. }
  if not WizardSilent then
    MsgBox(
      'Portable settings were copied to:' + #13#10 + MachineRoot + #13#10#13#10 +
      'Launch WinTAKTracker once so certificate passwords are re-protected for the Windows Service. ' +
      'You should not need to re-add servers if certs migrated successfully.',
      mbInformation, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Stop/remove existing service so files can be replaced on upgrade }
  WizardForm.StatusLabel.Caption := 'Stopping existing WinTAKTracker service…';
  DeleteServiceIfPresent;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Configuring Windows Service…';
    EnsureMachineStoreAcl;
    if WizardIsTaskSelected('migrateconfig') then
      MigratePortableConfig;
    CreateAndStartService;
  end;
end;

procedure RemoveHkcuRunValue;
var
  ResultCode: Integer;
begin
  { Start with Windows registers HKCU\...\Run\WinTAKTracker }
  if not Exec(
      ExpandConstant('{sys}\reg.exe'),
      'delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WinTAKTracker /f',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('reg delete Run value failed to launch')
  else
    Log('Removed HKCU Run\WinTAKTracker (exit ' + IntToStr(ResultCode) + ')');
end;

procedure RemoveMachineConfig;
var
  MachineRoot: String;
  ResultCode: Integer;
begin
  MachineRoot := ExpandConstant('{commonappdata}\WinTAKTracker');
  if DirExists(MachineRoot) then
  begin
    Log('Removing machine config ' + MachineRoot);
    Exec(ExpandConstant('{sys}\cmd.exe'),
      '/c rmdir /s /q "' + MachineRoot + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteServiceIfPresent;
    RemoveHkcuRunValue;
    { Optional wipe — default No (do not remove config/certs). }
    if not UninstallSilent then
    begin
      if MsgBox(
           'Also remove configuration and certificates under %ProgramData%\WinTAKTracker?' + #13#10#13#10 +
           'Choose No to keep servers, certs, and secrets for a reinstall.',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        RemoveMachineConfig;
    end;
  end;
end;
