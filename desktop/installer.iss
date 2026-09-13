#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef DotNetUrl
  #error DotNetUrl is required
#endif
#ifndef DotNetHash
  #error DotNetHash is required
#endif

[Setup]
#ifdef InstallerTest
AppId={{41CDE0F3-114A-4CAB-A50B-91D450877BE4}
AppName=FRP Panel Update Test
#else
AppId={{6A9160D7-25A5-4C4D-B459-E3FB45830810}
AppName=FRP Panel
#endif
AppVersion={#AppVersion}
AppPublisher=FRP Panel
DefaultDirName={localappdata}\Programs\FrpPanel
DefaultGroupName=FRP Panel
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
WizardStyle=modern
SetupIconFile=app.ico
UninstallDisplayIcon={app}\desktop\app\FrpPanel.exe
OutputDir={#ReleaseDir}
OutputBaseFilename=FRP-Panel-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "downloads\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "downloads\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
#include "legacy-runtime-files.iss"

[Icons]
Name: "{group}\FRP Panel"; Filename: "{app}\desktop\app\FrpPanel.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\FRP Panel"; Filename: "{app}\desktop\app\FrpPanel.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
#ifndef InstallerTest
Filename: "{app}\desktop\app\FrpPanel.exe"; Description: "启动 FRP Panel"; Flags: nowait postinstall skipifsilent; Check: not IsOnlineUpdate
Filename: "{app}\desktop\app\FrpPanel.exe"; Parameters: "--port {code:UpdatePortArgument}"; Flags: nowait; Check: IsOnlineUpdate; BeforeInstall: ReleaseUpdateMutex
#endif

[Code]
var
  RuntimeDownloadPage: TDownloadWizardPage;
  OnlineUpdate, OnlineMutexOwned: Boolean;
  OnlineReady, OnlineCommit, OnlineAbort, OnlineParent, OnlineBackend, OnlineMutex: THandle;
  OnlinePort: Integer;

function OpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function OpenEvent(Access: LongWord; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function CreateMutex(Attributes: LongWord; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function SignalEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function WaitHandle(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';

function IsOnlineUpdate: Boolean;
begin
  Result := OnlineUpdate;
end;

function UpdatePortArgument(Param: String): String;
begin
  Result := IntToStr(OnlinePort);
end;

procedure ReleaseUpdateMutex;
begin
  if OnlineMutexOwned then
  begin
    ReleaseMutex(OnlineMutex);
    OnlineMutexOwned := False;
  end;
end;

function InitializeSetup: Boolean;
var
  Nonce, Prefix: String;
  I, ParentPid, BackendPid: Integer;
begin
  Result := True;
  Nonce := ExpandConstant('{param:FRPUPDATE|}');
  OnlineUpdate := Nonce <> '';
  if not OnlineUpdate then Exit;
  Result := False;
  if Length(Nonce) <> 32 then Exit;
  for I := 1 to Length(Nonce) do
    if Pos(Nonce[I], '0123456789abcdef') = 0 then Exit;
  ParentPid := StrToIntDef(ExpandConstant('{param:FRPPID|0}'), 0);
  BackendPid := StrToIntDef(ExpandConstant('{param:FRPBACKENDPID|0}'), 0);
  OnlinePort := StrToIntDef(ExpandConstant('{param:FRPPORT|17600}'), 0);
  if (ParentPid <= 0) or (BackendPid <= 0) or (ParentPid = BackendPid) or (OnlinePort < 1024) or (OnlinePort > 65535) then Exit;
  Prefix := 'Local\FrpPanel-Update-' + Nonce;
  OnlineReady := OpenEvent($0002, False, Prefix + '-Ready');
  OnlineCommit := OpenEvent($00100000, False, Prefix + '-Commit');
  OnlineAbort := OpenEvent($00100000, False, Prefix + '-Abort');
  OnlineParent := OpenProcess($00100000, False, ParentPid);
  OnlineBackend := OpenProcess($00100000, False, BackendPid);
  OnlineMutex := CreateMutex(0, False, 'Local\FrpPanel-SingleInstance');
  Result := (OnlineReady <> 0) and (OnlineCommit <> 0) and (OnlineAbort <> 0) and
    (OnlineParent <> 0) and (OnlineBackend <> 0) and (OnlineMutex <> 0);
  if Result then Result := SignalEvent(OnlineReady);
end;

function WaitForUpdateShutdown: String;
var
  Attempts: Integer;
  WaitResult: LongWord;
begin
  Result := '';
  if not OnlineUpdate then Exit;
  Log('Online update: waiting for explicit handoff before changing files.');
  Attempts := 0;
  while WaitHandle(OnlineCommit, 100) <> 0 do
  begin
    Attempts := Attempts + 1;
    if (WaitHandle(OnlineAbort, 0) = 0) or (Attempts >= 600) then
    begin
      Result := '更新已取消或退出后台失败，当前文件未修改。请关闭安装程序后重试。';
      Exit;
    end;
  end;
  if WaitHandle(OnlineAbort, 0) = 0 then
  begin
    Result := '更新已取消，当前文件未修改。';
    Exit;
  end;
  if (WaitHandle(OnlineParent, 30000) <> 0) or (WaitHandle(OnlineBackend, 30000) <> 0) then
  begin
    Result := '旧版软件或后台尚未退出，请退出后重试。';
    Exit;
  end;
  if not OnlineMutexOwned then
  begin
    WaitResult := WaitHandle(OnlineMutex, 0);
    OnlineMutexOwned := (WaitResult = 0) or (WaitResult = $80);
  end;
  if not OnlineMutexOwned then
    Result := '另一个 FRP 面板已启动，请退出后重试更新。'
  else
    Log('Online update: desktop and backend exited; installation may proceed.');
end;

procedure DeinitializeSetup;
begin
  ReleaseUpdateMutex;
  if OnlineReady <> 0 then CloseHandle(OnlineReady);
  if OnlineCommit <> 0 then CloseHandle(OnlineCommit);
  if OnlineAbort <> 0 then CloseHandle(OnlineAbort);
  if OnlineParent <> 0 then CloseHandle(OnlineParent);
  if OnlineBackend <> 0 then CloseHandle(OnlineBackend);
  if OnlineMutex <> 0 then CloseHandle(OnlineMutex);
end;

procedure InitializeWizard;
begin
  RuntimeDownloadPage := CreateDownloadPage('安装运行环境', '正在下载 .NET 8 桌面运行时…', nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function HasFramework(const FrameworkName, MarkerFile: String): Boolean;
var
  Versions: TArrayOfString;
  RootKey, I, J, ViewIndex: Integer;
  InstallRoot, Version, Key: String;
  StableVersion: Boolean;
begin
  Result := False;
  Key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64';
  // The x64 .NET installer normally registers in the 32-bit registry view.
  for ViewIndex := 0 to 1 do
  begin
    if ViewIndex = 0 then RootKey := HKLM32 else RootKey := HKLM64;
    if not RegQueryStringValue(RootKey, Key, 'InstallLocation', InstallRoot) then
      InstallRoot := ExpandConstant('{commonpf64}\dotnet');
    if RegGetValueNames(RootKey, Key + '\sharedfx\' + FrameworkName, Versions) then
      for I := 0 to GetArrayLength(Versions) - 1 do
      begin
        Version := Versions[I];
        StableVersion := (Pos('8.0.', Version) = 1) and (Length(Version) > 4);
        for J := 5 to Length(Version) do
          if (Version[J] < '0') or (Version[J] > '9') then StableVersion := False;
        if StableVersion and FileExists(AddBackslash(InstallRoot) + 'dotnet.exe') and
          FileExists(AddBackslash(InstallRoot) + 'shared\' + FrameworkName + '\' + Version + '\' + MarkerFile) then
        begin
          Result := True;
          Exit;
        end;
      end;
  end;
end;

function HasDesktopRuntime: Boolean;
begin
  Result := HasFramework('Microsoft.WindowsDesktop.App', 'System.Windows.Forms.dll') and
    HasFramework('Microsoft.NETCore.App', 'coreclr.dll');
end;

function EnsureDesktopRuntime(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasDesktopRuntime then
  begin
    Log('.NET 8 Desktop Runtime x64 detected; skipping download and installation.');
    Exit;
  end;
  try
    RuntimeDownloadPage.Clear;
    RuntimeDownloadPage.Add('{#DotNetUrl}', 'dotnet-desktop-runtime-x64.exe', '{#DotNetHash}');
    RuntimeDownloadPage.Show;
    try
      RuntimeDownloadPage.Download;
    finally
      RuntimeDownloadPage.Hide;
    end;
  except
    Result := '.NET 下载或校验未完成，请检查网络后重试。' + #13#10 + GetExceptionMessage;
    Exit;
  end;
  WizardForm.StatusLabel.Caption := '正在安装 .NET 8 桌面运行时，请允许 Windows 管理员权限提示…';
  if not ShellExec('runas', ExpandConstant('{tmp}\dotnet-desktop-runtime-x64.exe'),
      '/install /quiet /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
    Result := '未能安装 .NET 桌面运行时，请允许管理员权限后重试。'
  else if (ExitCode = 3010) or (ExitCode = 1641) then
  begin
    NeedsRestart := True;
    Result := '.NET 安装完成，需要重启 Windows 后重新运行本安装程序。';
  end
  else if (ExitCode <> 0) or not HasDesktopRuntime then
    Result := Format('.NET 桌面运行时安装未完成（代码 %d），请重试。', [ExitCode]);
end;

function HasWebView2: Boolean;
var
  Version: String;
  Key: String;
begin
  Key := 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := (RegQueryStringValue(HKLM32, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
            (RegQueryStringValue(HKCU, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := WaitForUpdateShutdown;
  if Result <> '' then Exit;
  Result := EnsureDesktopRuntime(NeedsRestart);
  if Result <> '' then Exit;
  if not HasWebView2 then
  begin
    WizardForm.StatusLabel.Caption := '正在安装 WebView2，需要连接互联网…';
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := '无法启动 WebView2 安装程序。'
    else if not HasWebView2 then
      Result := 'WebView2 安装未完成，请检查网络后重试。';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Names: TArrayOfString;
  I: Integer;
  Value, ExePath: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ExePath := '"' + ExpandConstant('{app}\desktop\app\FrpPanel.exe') + '"';
    if RegGetValueNames(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names) then
      for I := 0 to GetArrayLength(Names) - 1 do
        if (Pos('FrpPanel-', Names[I]) = 1) and
          RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[I], Value) and
          (Pos(Lowercase(ExePath), Lowercase(Value)) = 1) then
          RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[I]);
  end;
end;
