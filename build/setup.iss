; The setup file of a CabinetOS release (docs\release.md, "The setup file"; ADR 0018): Inno Setup 6 around the
; release folder that build\release.ps1 has just made. release.ps1 compiles it and passes every fact below as a /D
; define read from that folder's release.json, so the setup never disagrees with the release it wraps.
;
; Per user, with no administrator rights: into %LOCALAPPDATA%\Programs\CabinetOS, the folder install.ps1 uses, so the
; in-app updater (ADR 0014) may change it. A Start Menu shortcut always, a desktop shortcut as an unchecked option,
; and "Start CabinetOS" checked at the end. The Settings > Apps entry is Inno's own (HKCU\...\Uninstall\CabinetOS_is1),
; which the updater keeps current after a swap (cabinetos-update, apps.rs). The uninstaller removes the whole install
; folder, with what the updater added later (previous\, previous-old\, a later version's new files); the user's
; settings, logs and update state under %APPDATA%\CabinetOS and %LOCALAPPDATA%\CabinetOS stay.
;
; The prerequisites are checked as install.ps1 checks them, before the wizard. A missing one is installed (ADR 0019):
; the setup downloads Microsoft's installer into its temporary folder, runs it silently, deletes it and checks again,
; in this order: the Windows App Runtime (for the user), the WebView2 Runtime (for the user when the setup is not
; elevated), the .NET runtime (for the whole PC, so Windows asks for administrator rights; a silent setup that is not
; elevated does not ask, and stops). The log names each URL, the file's size and SHA-256, and the installer's exit
; code. One still missing after that stops the setup with exit code 1 and the winget command that installs it.
;
; Silent: CabinetOS-<version>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=<file>
; /SKIPPREREQUISITECHECK installs even when a prerequisite looks missing, and downloads nothing.
; /PREREQTEST=1 only downloads the three installers, logs their sizes and hashes, and exits with code 1, having
; installed nothing, CabinetOS included: the test of the download code on a PC that must stay as it is.

#if VER < EncodeVer(6, 7, 0)
  #error Inno Setup 6.7 or newer is needed (the wizard's dynamic light and dark style): winget install --id JRSoftware.InnoSetup --exact --scope user
#endif
#ifndef AppVersion
  #error AppVersion is not defined: build\release.ps1 passes it, with the other facts, from release.json
#endif
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
#ifndef ReleaseDir
  #error ReleaseDir is not defined: the release folder this setup wraps
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\dist"
#endif
#ifndef OutputName
  #define OutputName "CabinetOS-" + AppVersion + "-win-x64-setup"
#endif
#ifndef WindowsBuild
  #define WindowsBuild "22621"
#endif
#ifndef DotnetName
  #define DotnetName "Microsoft.NETCore.App"
#endif
#ifndef DotnetVersion
  #define DotnetVersion "10.0.0"
#endif
#ifndef DotnetWinget
  #define DotnetWinget "Microsoft.DotNet.Runtime.10"
#endif
#ifndef DotnetInstaller
  #define DotnetInstaller "https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe"
#endif
#ifndef WebView2Installer
  #define WebView2Installer "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
#endif
#ifndef RuntimeName
  #define RuntimeName "Microsoft.WindowsAppRuntime.2"
#endif
#ifndef RuntimeVersion
  #define RuntimeVersion "2.5.1.0"
#endif
#ifndef RuntimeInstaller
  #define RuntimeInstaller "https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe"
#endif

[Setup]
; The Apps entry's key is the AppId with "_is1": cabinetos-update's SETUP_APPS_KEY names it. Never change it: an
; install made with another AppId is another program to Windows and to the updater.
AppId=CabinetOS
AppName=CabinetOS
AppVersion={#AppVersion}
AppVerName=CabinetOS {#AppVersion}
AppPublisher=CabinetOS
AppPublisherURL=https://github.com/OliverD25/cabinetos
AppSupportURL=https://github.com/OliverD25/cabinetos/issues
AppUpdatesURL=https://github.com/OliverD25/cabinetos/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductName=CabinetOS
VersionInfoDescription=CabinetOS setup
VersionInfoCopyright=Copyright (c) 2026 the CabinetOS authors. MIT license.
; The name without the version: the updater changes DisplayVersion after a swap, and a name with the old version in
; it would then be wrong.
UninstallDisplayName=CabinetOS
UninstallDisplayIcon={app}\CabinetOS.ico
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\CabinetOS
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.{#WindowsBuild}
WizardStyle=modern dynamic
SetupIconFile={#ReleaseDir}\CabinetOS.ico
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The release as the in-app update's swap installs it: every file but install.ps1, which runs from the unpacked zip
; only (cabinetos-update, swap.rs, NOT_INSTALLED). Never the .pdb symbols: release.ps1 moves them into the symbols zip
; (docs\release.md, "The symbols"), and the exclude keeps them out of a setup made from a folder that still holds some.
Source: "{#ReleaseDir}\*"; DestDir: "{app}"; Excludes: "install.ps1,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CabinetOS"; Filename: "{app}\CabinetOS.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CabinetOS.ico"; Comment: "CabinetOS file manager"
Name: "{autodesktop}\CabinetOS"; Filename: "{app}\CabinetOS.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CabinetOS.ico"; Comment: "CabinetOS file manager"; Tasks: desktopicon

[Run]
Filename: "{app}\CabinetOS.exe"; Description: "Start CabinetOS"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The install folder also when it was there before the setup: Inno removes only a folder it made itself, so an empty
; folder an earlier uninstall left behind stayed after every later uninstall (seen in the VM on 2026-10-02).
Type: dirifempty; Name: "{app}"

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  NewLine = #13#10;
  RuntimeFile = 'windowsappruntimeinstall-x64.exe';
  WebView2File = 'MicrosoftEdgeWebview2Setup.exe';
  DotnetFile = 'dotnet-runtime-setup.exe';

// The Index-th number (from 0) of a version such as 10.0.12 or 10.0.0-rc.2; -1 when there is none.
function VersionNumber(Text: String; Index: Integer): Integer;
var
  Dash, Dot, I: Integer;
begin
  Dash := Pos('-', Text);
  if Dash > 0 then
    Text := Copy(Text, 1, Dash - 1);
  for I := 1 to Index do
  begin
    Dot := Pos('.', Text);
    if Dot = 0 then
      Text := ''
    else
      Text := Copy(Text, Dot + 1, Length(Text));
  end;
  Dot := Pos('.', Text);
  if Dot > 0 then
    Text := Copy(Text, 1, Dot - 1);
  Result := StrToIntDef(Text, -1);
end;

// A's first four numbers against B's: below 0, 0, or above 0. A missing number counts as 0.
function CompareVersions(const A, B: String): Integer;
var
  I, X, Y: Integer;
begin
  Result := 0;
  for I := 0 to 3 do
  begin
    X := VersionNumber(A, I);
    Y := VersionNumber(B, I);
    if X < 0 then
      X := 0;
    if Y < 0 then
      Y := 0;
    if X <> Y then
    begin
      Result := X - Y;
      Exit;
    end;
  end;
end;

// Whether Root\shared\Name holds Wanted or a newer version of the same major, as the .NET host requires.
function HasFramework(const Root, Name, Wanted: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;
  if FindFirst(AddBackslash(Root) + 'shared\' + Name + '\*', Found) then
  try
    repeat
      if ((Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0)
        and (VersionNumber(Found.Name, 0) = VersionNumber(Wanted, 0))
        and (CompareVersions(Found.Name, Wanted) >= 0) then
      begin
        Log('found ' + Name + ' ' + Found.Name + ' in ' + Root);
        Result := True;
      end;
    until Result or not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

// The places the .NET host looks, in its order: the variables, the registered install location, Program Files.
function DotnetFound(): Boolean;
var
  Location: String;
begin
  Result := HasFramework(GetEnv('DOTNET_ROOT_X64'), '{#DotnetName}', '{#DotnetVersion}')
    or HasFramework(GetEnv('DOTNET_ROOT'), '{#DotnetName}', '{#DotnetVersion}');
  if not Result and RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    Result := HasFramework(Location, '{#DotnetName}', '{#DotnetVersion}');
  if not Result and RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then
    Result := HasFramework(Location, '{#DotnetName}', '{#DotnetVersion}');
  if not Result then
    Result := HasFramework(ExpandConstant('{commonpf64}\dotnet'), '{#DotnetName}', '{#DotnetVersion}');
end;

// The Windows App Runtime is a framework package: Get-AppxPackage, as install.ps1 asks it, is the one sure answer.
// 0: found; 1: missing; 2: it could not be asked.
function AppRuntimeState(): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "if (@(Get-AppxPackage -Name ''{#RuntimeName}'' | Where-Object { [string]$_.Architecture -eq ''X64'' -and [version]$_.Version -ge [version]''{#RuntimeVersion}'' }).Count -gt 0) { exit 0 } else { exit 3 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := 2
  else if Code = 0 then
    Result := 0
  else if Code = 3 then
    Result := 1
  else
    Result := 2;
end;

function WebView2Found(): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0');
  if not Result then
    Result := RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0');
end;

function SkipPrerequisiteCheck(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/SKIPPREREQUISITECHECK') = 0 then
      Result := True;
end;

function PrerequisiteTest(): Boolean;
var
  Value: String;
begin
  Value := ExpandConstant('{param:PREREQTEST|}');
  Result := (Value <> '') and (Value <> '0');
end;

// Downloads Url into the setup's temporary folder as Name and logs its size and SHA-256. No hash is pinned: the
// links are Microsoft's "latest" links, whose bytes change with each runtime release (ADR 0019); the log line is the
// trace. The file's path, or '' with Error set.
function DownloadInstaller(const Url, Name: String; var Error: String): String;
var
  Size: Int64;
begin
  Result := '';
  Log('downloading ' + Url);
  try
    Size := DownloadTemporaryFile(Url, Name, '', nil);
    Result := ExpandConstant('{tmp}\') + Name;
    Log('downloaded ' + Name + ': ' + IntToStr(Size) + ' bytes, SHA-256 ' + GetSHA256OfFile(Result));
  except
    Error := 'the download of ' + Url + ' failed: ' + GetExceptionMessage;
    Log(Error);
  end;
end;

// Downloads one prerequisite's installer, runs it silently, waits, and deletes it. AsAdmin runs it through Windows'
// administrator prompt (UAC) unless the setup is elevated already. What happened, for the message when the
// prerequisite is still missing afterwards.
function InstallPrerequisite(const Url, Name, Params: String; AsAdmin: Boolean): String;
var
  Path, Error: String;
  Code: Integer;
  Started: Boolean;
begin
  Error := '';
  Path := DownloadInstaller(Url, Name, Error);
  if Path = '' then
  begin
    Result := Error;
    Exit;
  end;
  Log('running ' + Name + ' ' + Params);
  if AsAdmin and not IsAdmin() then
    Started := ShellExec('runas', Path, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code)
  else
    Started := Exec(Path, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Started then
    Result := 'Microsoft''s installer ended with exit code ' + IntToStr(Code)
  else
    Result := Name + ' did not start: ' + SysErrorMessage(Code) + ' (code ' + IntToStr(Code) + ')';
  Log(Result);
  DeleteFile(Path);
end;

// /PREREQTEST: the three downloads and their log lines, and nothing installed.
procedure TestDownloads();
var
  Path, Error: String;
  Done: Integer;
begin
  Log('/PREREQTEST: the three installers are downloaded and deleted; nothing is installed');
  Done := 0;
  Path := DownloadInstaller('{#RuntimeInstaller}', RuntimeFile, Error);
  if Path <> '' then
  begin
    Done := Done + 1;
    DeleteFile(Path);
  end;
  Path := DownloadInstaller('{#WebView2Installer}', WebView2File, Error);
  if Path <> '' then
  begin
    Done := Done + 1;
    DeleteFile(Path);
  end;
  Path := DownloadInstaller('{#DotnetInstaller}', DotnetFile, Error);
  if Path <> '' then
  begin
    Done := Done + 1;
    DeleteFile(Path);
  end;
  Log('/PREREQTEST: ' + IntToStr(Done) + ' of 3 downloads succeeded; the setup exits without installing anything');
end;

function RuntimeText(): String;
begin
  Result := '- Windows App Runtime {#RuntimeVersion} or newer (x64)' + NewLine
    + '    winget install --id {#RuntimeName} --exact' + NewLine
    + '    If winget offers a version older than {#RuntimeVersion}, use Microsoft''s installer instead:' + NewLine
    + '    {#RuntimeInstaller}' + NewLine;
end;

function WebView2Text(): String;
begin
  Result := '- Microsoft Edge WebView2 Runtime' + NewLine
    + '    winget install --id Microsoft.EdgeWebView2Runtime --exact' + NewLine;
end;

function DotnetText(): String;
begin
  Result := '- .NET ' + IntToStr(VersionNumber('{#DotnetVersion}', 0)) + ' runtime ({#DotnetName} {#DotnetVersion} or a newer '
    + IntToStr(VersionNumber('{#DotnetVersion}', 0)) + '.x)' + NewLine
    + '    winget install --id {#DotnetWinget} --exact' + NewLine;
end;

// Before the wizard: every prerequisite; the missing ones installed from Microsoft's installers, and a message with
// the commands that install those still missing after that.
function InitializeSetup(): Boolean;
var
  Missing, Wanted, Tried: String;
  Runtime: Integer;
  NeedRuntime, NeedWebView2, NeedDotnet, Declined: Boolean;
begin
  Result := True;
  if PrerequisiteTest() then
  begin
    TestDownloads();
    Result := False;
    Exit;
  end;
  if SkipPrerequisiteCheck() then
  begin
    Log('/SKIPPREREQUISITECHECK: the prerequisites are not checked');
    Exit;
  end;
  Runtime := AppRuntimeState();
  if Runtime = 2 then
    Log('could not check the Windows App Runtime (Windows PowerShell did not answer); CabinetOS needs {#RuntimeVersion} or newer');
  NeedRuntime := Runtime = 1;
  NeedWebView2 := not WebView2Found();
  NeedDotnet := not DotnetFound();
  if not (NeedRuntime or NeedWebView2 or NeedDotnet) then
  begin
    Log('the prerequisites are there');
    Exit;
  end;

  // A double-click would otherwise show nothing for the minutes the downloads take (the Windows App Runtime's
  // installer alone is about 120 MB), and then an administrator prompt with no context.
  Declined := False;
  if not WizardSilent() then
  begin
    Wanted := '';
    if NeedRuntime then
      Wanted := Wanted + '- Windows App Runtime {#RuntimeVersion} (x64)' + NewLine;
    if NeedWebView2 then
      Wanted := Wanted + '- Microsoft Edge WebView2 Runtime' + NewLine;
    if NeedDotnet then
      Wanted := Wanted + '- .NET ' + IntToStr(VersionNumber('{#DotnetVersion}', 0)) + ' runtime' + NewLine;
    Wanted := 'CabinetOS needs these parts of Windows from Microsoft, and this PC does not have them yet:' + NewLine + NewLine
      + Wanted + NewLine + 'The setup now downloads them from Microsoft and installs them. This can take a few minutes.';
    if NeedDotnet and not IsAdmin() then
      Wanted := Wanted + ' The .NET runtime installs for every user of this PC, so Windows asks for administrator rights.';
    Wanted := Wanted + NewLine + NewLine + 'Cancel stops the setup and lists the commands that install them by hand.';
    Declined := SuppressibleMsgBox(Wanted, mbConfirmation, MB_OKCANCEL, IDOK) <> IDOK;
    if Declined then
      Log('the downloads were declined');
  end;

  Missing := '';
  if NeedRuntime then
  begin
    if Declined then
      Tried := 'not tried: the download was declined'
    else
      Tried := InstallPrerequisite('{#RuntimeInstaller}', RuntimeFile, '--quiet', False);
    if AppRuntimeState() = 1 then
      Missing := Missing + RuntimeText() + '    The setup''s try: ' + Tried + NewLine;
  end;
  if NeedWebView2 then
  begin
    if Declined then
      Tried := 'not tried: the download was declined'
    else
      Tried := InstallPrerequisite('{#WebView2Installer}', WebView2File, '/silent /install', False);
    if not WebView2Found() then
      Missing := Missing + WebView2Text() + '    The setup''s try: ' + Tried + NewLine;
  end;
  if NeedDotnet then
  begin
    if Declined then
      Tried := 'not tried: the download was declined'
    else if WizardSilent() and not IsAdmin() then
    begin
      // A silent setup has nobody to answer the administrator prompt (winget runs it so): it does not ask.
      Tried := 'not tried: the .NET runtime installs for the whole PC and needs administrator rights, which a silent setup does not ask for';
      Log(Tried);
    end
    else
      Tried := InstallPrerequisite('{#DotnetInstaller}', DotnetFile, '/install /quiet /norestart', True);
    if not DotnetFound() then
      Missing := Missing + DotnetText() + '    The setup''s try: ' + Tried + NewLine;
  end;

  if Missing <> '' then
  begin
    Missing := 'CabinetOS needs these first:' + NewLine + NewLine + Missing + NewLine
      + 'Install them, then run this setup again.';
    Log(Missing);
    SuppressibleMsgBox(Missing, mbCriticalError, MB_OK, IDOK);
    Result := False;
  end
  else
    Log('the prerequisites are there now');
end;

// An install by install.ps1 in the same folder has its own record, uninstaller and Apps entry: the two must not mix.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\.cabinetos-install.json')) then
    Result := 'CabinetOS is installed in ' + ExpandConstant('{app}') + ' by install.ps1. Remove that install first, then run this setup again; '
      + 'your settings stay:' + NewLine + 'powershell -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\uninstall.ps1') + '"';
end;

// The CabinetOS programs in Folder, previous\ and previous-old\ that a running process holds: Windows refuses to
// open a running program's file for writing. WMI could name the processes, but it waits for a service: right after a
// restart the VM's uninstall took 22 minutes on 2026-10-02 (a second once the VM had settled). This test needs no
// service and asks what the uninstall needs.
function ProgramsInUse(const Folder: String): String;
var
  Names, Dirs: TArrayOfString;
  I, J: Integer;
  Path: String;
  Stream: TFileStream;
begin
  Result := '';
  SetArrayLength(Names, 5);
  Names[0] := 'CabinetOS.exe';
  Names[1] := 'cabinetos-core.exe';
  Names[2] := 'cabinetos-cli.exe';
  Names[3] := 'cab.exe';
  Names[4] := 'cabinetos-indexer.exe';
  SetArrayLength(Dirs, 3);
  Dirs[0] := Folder;
  Dirs[1] := Folder + '\previous';
  Dirs[2] := Folder + '\previous-old';
  for J := 0 to GetArrayLength(Dirs) - 1 do
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Path := Dirs[J] + '\' + Names[I];
      if FileExists(Path) then
        try
          Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareDenyNone);
          Stream.Free;
        except
          Result := Result + ' ' + Path;
        end;
    end;
end;

// A running program's files cannot be deleted: the uninstall would leave half a CabinetOS behind.
function InitializeUninstall(): Boolean;
var
  Running: String;
begin
  Running := ProgramsInUse(ExpandConstant('{app}'));
  Result := Running = '';
  if not Result then
  begin
    Log('CabinetOS runs, these files are in use:' + Running);
    SuppressibleMsgBox('Close CabinetOS first. These files are in use:' + Running, mbError, MB_OK, IDOK);
  end;
end;

// Before Inno removes what it installed: everything in the install folder but Inno's own uninstaller files, so what
// the in-app updater added (previous\, previous-old\, the files a later version brought) goes too, and Inno's removal
// of the folder finds it empty. Run after that removal, this left an empty folder behind (seen in the VM on
// 2026-10-02). Only a folder named CabinetOS, never a drive's root; Inno's uninstaller files go last, by Inno, and the
// folder with them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  App: String;
  Found: TFindRec;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  App := RemoveBackslash(ExpandConstant('{app}'));
  if (CompareText(ExtractFileName(App), 'CabinetOS') <> 0) or (Length(App) <= 3) then
  begin
    Log('the install folder ' + App + ' is not named CabinetOS; what the updater added there stays');
    Exit;
  end;
  if FindFirst(App + '\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..') and (CompareText(Copy(Found.Name, 1, 5), 'unins') <> 0) then
      begin
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          DelTree(App + '\' + Found.Name, True, True, True)
        else
          DeleteFile(App + '\' + Found.Name);
        Log('removed: ' + Found.Name);
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;
