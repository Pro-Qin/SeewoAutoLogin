; 希沃自动登录安装脚本
; 使用 Inno Setup 编译此脚本生成安装程序
;   iscc setup.iss                     → 轻量安装包（不内置 WebView2；运行时缺失时由程序自动下载安装）
;   iscc /DBundleWebView2=1 setup.iss  → 内置 WebView2 版（内嵌官方离线运行时安装器，
;                                        需先把安装器放到 publish\MicrosoftEdgeWebView2RuntimeInstallerX64.exe）
; 官方离线安装器下载：https://go.microsoft.com/fwlink/?linkid=2124701（约 203MB，断网也能装）

; 本地构建用的默认版本号；CI（release.yml / build.yml）会在编译前用 csproj 里的 <Version> 覆盖这一行，
; 避免出现“发布 vX.Y.Z，安装包却叫 vA.B.C”的问题。
#define AppVersion "1.10.2"

#ifndef BundleWebView2
  #define BundleWebView2 0
#endif

#if BundleWebView2
  #define OutputSuffix "_WithWebView2"
#else
  #define OutputSuffix ""
#endif

[Setup]
AppName=希沃自动登录
AppVersion={#AppVersion}
AppPublisher=SeewoAutoLogin
AppPublisherURL=https://github.com/Pro-Qin/SeewoAutoLogin
DefaultDirName={autopf}\SeewoAutoLogin
DefaultGroupName=希沃自动登录
DisableProgramGroupPage=yes
OutputDir=.\publish
OutputBaseFilename=SeewoAutoLogin_Setup_v{#AppVersion}{#OutputSuffix}
SetupIconFile=Resources\app.ico
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\SeewoAutoLogin.exe
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "启动选项："

[Registry]
; 「开机自动启动」任务：勾选后写入当前用户启动项（与程序内设置、欢迎界面选项共用同一个值），卸载时自动删除
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SeewoAutoLogin"; ValueData: """{app}\SeewoAutoLogin.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Files]
Source: "bin\Release\net8.0-windows10.0.19041.0\publish\SeewoAutoLogin.exe"; DestDir: "{app}"; Flags: ignoreversion
; 如果使用非单文件发布，取消下面注释
; Source: "bin\Release\net8.0-windows10.0.19041.0\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

#if BundleWebView2
; 内置 WebView2：解开到临时目录，安装时静默运行，装完自动删除（不留在安装目录）
Source: "publish\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\希沃自动登录"; Filename: "{app}\SeewoAutoLogin.exe"
Name: "{commondesktop}\希沃自动登录"; Filename: "{app}\SeewoAutoLogin.exe"; Tasks: desktopicon
Name: "{group}\卸载希沃自动登录"; Filename: "{uninstallexe}"

[Run]
#if BundleWebView2
; 已装 WebView2 时跳过（Check 为 False 则不执行），未装则离线静默安装
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 运行时（离线安装，约需 1-2 分钟）..."; Flags: runhidden waituntilterminated; Check: WebView2Missing
#endif
Filename: "{app}\SeewoAutoLogin.exe"; Description: "启动希沃自动登录"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\SeewoAutoLogin.exe"; Parameters: "--uninstall"; RunOnceId: "SeewoAutoLoginCleanup"

[Code]
{ 是否缺少 WebView2 运行时。判断不出来时返回 True，让官方安装器自行判断（已装则它会直接跳过）。 }
function WebView2Missing: Boolean;
var
  Version: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and
     (Version <> '') and (Version <> '0.0.0.0') then
    Result := False
  else if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and
     (Version <> '') and (Version <> '0.0.0.0') then
    Result := False;
end;
