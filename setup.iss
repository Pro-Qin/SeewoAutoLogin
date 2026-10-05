; 希沃自动登录安装脚本 —— Windows 7 兼容分支
; 使用 Inno Setup 编译此脚本生成安装程序
;   iscc setup.iss                     → 轻量安装包（不内置 WebView2；运行时缺失时由程序自动下载安装）
;   iscc /DBundleWebView2=1 setup.iss  → 内置 WebView2 版（内嵌官方离线运行时安装器，
;                                        需先把安装器放到 publish\MicrosoftEdgeWebView2RuntimeInstallerX64.exe）
;
; ── 本分支与主干（main）的四处差异 ──────────────────────────────────────────────
; 1) 目标框架是 net48（.NET Framework 4.8），Win7 SP1 可装；主干是 .NET 8 / Win10 19041+。
;    所以这里 MinVersion 放宽到 6.1sp1，并在 [Code] 里显式检测 .NET Framework 4.8。
; 2) 产物是 x64：WebView2Loader.dll 是原生 DLL，位数必须与进程一致，
;    32 位系统会被 ArchitecturesAllowed 直接拒绝（装了也起不来）。
; 3) net48 不支持单文件发布，[Files] 改成整目录拷贝。
; 4) Win7 上 WebView2 运行时最高只能到 109.0.1518.78（之后微软不再支持 Win7/8.1）：
;    - 内置版请放这个版本的离线安装器，更高版本在 Win7 上装完也起不来；
;    - 轻量版的自动安装只在 Win10/11 走 Evergreen，Win7 上程序会引导手动安装 109。
;
; 官方下载入口：https://developer.microsoft.com/microsoft-edge/webview2/
; （固定版本 Fixed Version → x64；安装器体积约 100-200MB，断网也能装）

; 本地构建用的默认版本号；CI（release.yml / build.yml）会在编译前用 csproj 里的 <Version> 覆盖这一行，
; 避免出现“发布 vX.Y.Z，安装包却叫 vA.B.C”的问题。
#define AppVersion "1.14.1"

; Win7 上 WebView2 运行时的最后一版（再高的版本不支持 Win7/8.1）
#define WebView2RuntimeVersion "109.0.1518.78"

#ifndef BundleWebView2
  #define BundleWebView2 0
#endif

#if BundleWebView2
  #define OutputSuffix "_Win7_WithWebView2"
#else
  #define OutputSuffix "_Win7"
#endif

[Setup]
AppName=SeewoAutoLogin
AppVersion={#AppVersion}
AppPublisher=SeewoAutoLogin
AppPublisherURL=https://github.com/Pro-Qin/SeewoAutoLogin
DefaultDirName={autopf}\SeewoAutoLogin
DefaultGroupName=SeewoAutoLogin
DisableProgramGroupPage=yes
OutputDir=.\publish
OutputBaseFilename=SeewoAutoLogin_Setup_v{#AppVersion}{#OutputSuffix}
SetupIconFile=Resources\app.ico
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\SeewoAutoLogin.exe
PrivilegesRequired=admin
; Win7 SP1 起（6.1.7601）。主干分支这里是 MinVersion=10.0.19041。
MinVersion=6.1sp1
; 产物是 x64，32 位系统直接拒绝，避免「装完打不开」
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

; 【说明】这里原本有一段 [Registry]，用于按安装选项写入/清理 HKCU 启动项。
; 安装器不再提供「开机自动启动」：该选项与首次启动的欢迎界面重复，且安装器里的勾选默认开启，
; 用户往往在不知情的情况下被写入启动项。开机自启改为完全由欢迎界面 /「设置 → 常规」决定，
; 历史残留的启动项会由程序内的自启开关负责清理，因此这里不再声明任何注册表条目。

[Files]
; net48 不支持单文件发布，整目录拷贝（主 exe + 依赖 DLL + exe.config）
Source: "bin\Release\net48\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#if BundleWebView2
; 内置 WebView2：解开到临时目录，安装时静默运行，装完自动删除（不留在安装目录）
; 必须是 {#WebView2RuntimeVersion} 或更早的离线安装器，更高版本在 Win7 上不可用
Source: "publish\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[InstallDelete]
; 清理旧版本以希沃自动登录命名的快捷方式，避免主标题改名后残留
Type: files; Name: "{commondesktop}\希沃自动登录.lnk"
Type: files; Name: "{group}\希沃自动登录.lnk"

[Icons]
Name: "{group}\SeewoAutoLogin"; Filename: "{app}\SeewoAutoLogin.exe"
Name: "{commondesktop}\SeewoAutoLogin"; Filename: "{app}\SeewoAutoLogin.exe"; Tasks: desktopicon
Name: "{group}\卸载 SeewoAutoLogin"; Filename: "{uninstallexe}"

[Run]
#if BundleWebView2
; 已装 WebView2 时跳过（Check 为 False 则不执行），未装则离线静默安装
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 运行时（离线安装，约需 1-2 分钟）..."; Flags: runhidden waituntilterminated; Check: WebView2Missing
#endif
; 刷新 Windows 图标缓存。
; 快捷方式本身会被安装程序重建，但桌面/开始菜单上的图标来自系统缓存：
; 程序换了图标之后，缓存里仍是旧的，用户看到的就还是旧图标。这一步让资源管理器重新读取。
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist
Filename: "{app}\SeewoAutoLogin.exe"; Description: "启动 SeewoAutoLogin"; Flags: nowait postinstall skipifsilent
; 静默升级（/SILENT）时上面那条会被 skipifsilent 跳过，装完程序停在关闭状态，用户得手动再开一次。
; 这里补一条只在静默模式下执行的启动项，让静默升级后程序自动回到托盘。
Filename: "{app}\SeewoAutoLogin.exe"; Parameters: "--elevated --minimized --updated"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{app}\SeewoAutoLogin.exe"; Parameters: "--uninstall"; RunOnceId: "SeewoAutoLoginCleanup"
; 卸载后同样刷新图标缓存，避免残留的旧图标
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist; RunOnceId: "RefreshIconCache"

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

{ .NET Framework 4.8 是否已安装：注册表 Release 值 >= 528040（Win7 上 4.8 写的是 528049）。
  Win7 默认不带 4.8，缺了它程序在任何提示出现之前就会弹「应用程序无法启动」。 }
function IsDotNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and
            (Release >= 528040);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := False;

  if not IsDotNet48Installed then
  begin
    if MsgBox('未检测到 .NET Framework 4.8，本程序无法启动。' + #13#10 + #13#10 +
              'Windows 7 SP1 需要先安装 .NET Framework 4.8（安装前请确认系统已打好 SP1 补丁）。' + #13#10 +
              '是否现在打开微软官方下载页？', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48',
                '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Exit;
  end;

  Result := True;
end;
