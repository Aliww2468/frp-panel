#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef AppVersion
  #define AppVersion "1.1.0"
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
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "downloads\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\FRP Panel"; Filename: "{app}\desktop\app\FrpPanel.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\FRP Panel"; Filename: "{app}\desktop\app\FrpPanel.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\desktop\app\FrpPanel.exe"; Description: "启动 FRP Panel"; Flags: nowait postinstall skipifsilent

[Code]
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
  Result := '';
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
