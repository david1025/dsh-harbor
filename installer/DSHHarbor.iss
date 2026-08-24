; Build the framework-dependent release before compiling this script:
; dotnet publish .\DSHHarbor\DSHHarbor.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\win-x64

#define MyAppName "DSH Harbor"
#define MyAppPublisher "DSH Harbor Community"
#define MyAppExeName "DSHHarbor.exe"

[Setup]
AppId={{E7415235-805D-4B5F-91F7-5F23E25E8B65}
AppName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppVersion=0.2.0
DefaultDirName={autopf}\DSH Harbor
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=DSHHarborSetup-0.2.0
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\DSHHarbor\Assets\dsh-harbor.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[InstallDelete]
Type: files; Name: "{autoprograms}\DSH Desktop.lnk"
Type: files; Name: "{autodesktop}\DSH Desktop.lnk"
Type: files; Name: "{app}\DSHDesktop.exe"

[UninstallDelete]
Type: files; Name: "{app}\DSHDesktop.exe"
Type: files; Name: "{autoprograms}\DSH Desktop.lnk"
Type: files; Name: "{autodesktop}\DSH Desktop.lnk"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.8/windowsdesktop-runtime-10.0.8-win-x64.exe';
  DotNetRuntimeFile = 'windowsdesktop-runtime-10.0.8-win-x64.exe';
  DotNetRuntimeSha256 = '378866DDBC70116F0B83E88D1F7861271172813B5F7D8A59BF12FC992BF65786';
  WebViewRuntimeUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebViewRuntimeFile = 'MicrosoftEdgeWebView2Setup.exe';
  WebViewRuntimeSha256 = '94314D8B20C8A370DF81C5CC3D8D7A3E23FE5DE14EF5E988229FF3208E449146';

var
  DownloadPage: TDownloadWizardPage;
  NeedDotNetRuntime: Boolean;
  NeedWebViewRuntime: Boolean;

function HasDotNet10DesktopRuntime: Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  { Some SDK-based installations do not expose the shared framework in this
    registry location. The runtime directory is the authoritative fallback. }
  Result := DirExists(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.8'));
  if Result then exit;
  if RegGetSubkeyNames(HKLM64,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
       Versions) then
  begin
    for Index := 0 to GetArrayLength(Versions) - 1 do
    begin
      if Pos('10.', Versions[Index]) = 1 then
      begin
        Result := True;
        exit;
      end;
    end;
  end;
end;

function HasWebView2Runtime: Boolean;
begin
  { Windows 11 normally carries the Evergreen runtime in Program Files (x86). }
  Result := DirExists(ExpandConstant('{pf32}\Microsoft\EdgeWebView\Application')) or
            DirExists(ExpandConstant('{pf}\Microsoft\EdgeWebView\Application'));
end;

procedure InitializeWizard;
begin
  NeedDotNetRuntime := not HasDotNet10DesktopRuntime;
  NeedWebViewRuntime := not HasWebView2Runtime;
  DownloadPage := CreateDownloadPage('正在下载运行环境', '首次安装需要准备 Windows 运行环境。', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrorMessage: String;
begin
  Result := True;
  if CurPageID <> wpReady then exit;

  DownloadPage.Clear;
  if NeedDotNetRuntime then
    DownloadPage.Add(DotNetRuntimeUrl, DotNetRuntimeFile, DotNetRuntimeSha256);
  if NeedWebViewRuntime then
    DownloadPage.Add(WebViewRuntimeUrl, WebViewRuntimeFile, WebViewRuntimeSha256);

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      ErrorMessage := Format('%s: %s', [DownloadPage.LastBaseNameOrUrl, GetExceptionMessage]);
      SuppressibleMsgBox(AddPeriod(ErrorMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure InstallPrerequisite(const FileName, Parameters, DisplayName: String);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{tmp}\' + FileName), Parameters, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    RaiseException('无法启动 ' + DisplayName + ' 安装程序。');
  if (ResultCode <> 0) and (ResultCode <> 3010) then
    RaiseException(DisplayName + ' 安装失败，退出码：' + IntToStr(ResultCode));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssInstall then exit;
  if NeedDotNetRuntime then
    InstallPrerequisite(DotNetRuntimeFile, '/install /quiet /norestart', '.NET Desktop Runtime');
  if NeedWebViewRuntime then
    InstallPrerequisite(WebViewRuntimeFile, '/silent /install', 'Microsoft Edge WebView2 Runtime');
end;
