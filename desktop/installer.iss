#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef DotNetUrl
  #error DotNetUrl is required
#endif
#ifndef DotNetHash
  #error DotNetHash is required
#endif

[Setup]
AppId={{6A9160D7-25A5-4C4D-B459-E3FB45830810}
AppName=FRP Panel
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
Filename: "{app}\desktop\app\FrpPanel.exe"; Description: "启动 FRP Panel"; Flags: nowait postinstall skipifsilent

[Code]
var
  RuntimeDownloadPage: TDownloadWizardPage;

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
