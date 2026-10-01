; 枪声分层工作台 安装包（Windows x64）
; 用法：ISCC.exe setup.iss
; 产物：..\dist\GunMix_Setup_0.2.0.exe

#define AppName "枪声分层工作台"
#define AppNameEn "GunMix"
#define AppVersion "0.2.0"
#define AppPublisher "EZ4"
#define AppExeName "GunMix.App.exe"
; AppId 一旦确定不要修改，否则升级安装会被识别为另一个程序
#define AppIdGuid "{{9A4F2C1D-7B3E-4E8A-A6D2-5F1C8B7E3A90}"

[Setup]
AppId={#AppIdGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename={#AppNameEn}_Setup_{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
PrivilegesRequired=admin
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableWelcomePage=no

[Languages]
Name: chinesesimplified; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; GroupDescription: "其他图标："

[Files]
; 自包含安装包（.NET 运行时已内置，无需另行安装 .NET）
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\使用说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.md"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
