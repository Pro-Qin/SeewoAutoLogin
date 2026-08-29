; 希沃自动登录安装脚本
; 使用 Inno Setup 编译此脚本生成安装程序

[Setup]
AppName=希沃自动登录
AppVersion=1.7.9
AppPublisher=SeewoAutoLogin
AppPublisherURL=https://github.com/CJKmkp/SeewoAutoLogin
DefaultDirName={autopf}\SeewoAutoLogin
DefaultGroupName=希沃自动登录
DisableProgramGroupPage=yes
OutputDir=.\publish
OutputBaseFilename=SeewoAutoLogin_Setup_v1.7.9
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

[Files]
Source: "bin\Release\net8.0-windows10.0.19041.0\publish\SeewoAutoLogin.exe"; DestDir: "{app}"; Flags: ignoreversion
; 如果使用非单文件发布，取消下面注释
; Source: "bin\Release\net8.0-windows10.0.19041.0\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\希沃自动登录"; Filename: "{app}\SeewoAutoLogin.exe"
Name: "{commondesktop}\希沃自动登录"; Filename: "{app}\SeewoAutoLogin.exe"; Tasks: desktopicon
Name: "{group}\卸载希沃自动登录"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\SeewoAutoLogin.exe"; Description: "启动希沃自动登录"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\SeewoAutoLogin.exe"; Parameters: "--uninstall"
