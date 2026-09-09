; Rivulet Light 安装程序脚本（Inno Setup 7）
; 编译：ISCC.exe installer.iss
; 说明：按用户级安装（无需管理员权限），默认安装到
;       %LOCALAPPDATA%\Programs\RivuletLight，卸载时保留用户设置与日志。

#define MyAppName "Rivulet Light"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "Lenkmat"
#define MyAppExeName "RivuletLight.exe"
#define MyAppURL "https://github.com/Lenkmat/Rivulet-Light"

[Setup]
AppId={{7E1C2F4B-9A3D-4E86-B5C0-2A7D8F13E5A1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\RivuletLight
DisableDirPage=no
DisableProgramGroupPage=yes
; 输出：d:\Rivulet Light\installer\RivuletLight-Setup-1.0.1-x64.exe
OutputDir=.
OutputBaseFilename=RivuletLight-Setup-{#MyAppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 用户级安装（asInvoker 清单配套，无需 UAC 提权）
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 发布产物整体安装（自包含运行时 + 未嵌入单文件的 SDK 投影 DLL）；排除 pdb
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 停止可能仍在运行的实例，避免卸载残留文件锁
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F >nul 2>&1"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; 覆盖层可能遗留的渲染临时文件（用户设置 %LOCALAPPDATA%\RivuletLight 保留不删）

[Code]
// 卸载完成后清理用户运行数据前的确认留空——默认保留用户设置与日志
