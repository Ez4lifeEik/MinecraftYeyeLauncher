; ============================================================
;  ArclightLauncher  Inno Setup 6 脚本
;  输出: publish\ArclightLauncher-Setup-0.3.1.exe
; ============================================================

#define AppName      "ArclightLauncher"
#define AppVersion   "0.3.5"
#define AppPublisher "Arclight Team"
#define AppExeName   "ArclightLauncher.exe"
#define PublishDir   "publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppVerName={#AppName} {#AppVersion}

; 安装到 %LocalAppData%\Programs（无需管理员权限）
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir={#PublishDir}
OutputBaseFilename=ArclightLauncher-Setup-{#AppVersion}

; 压缩
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; 窗口外观
WizardStyle=modern
WizardSizePercent=120

; 图标（如存在）
; SetupIconFile=Assets\icon.ico
; UninstallDisplayIcon={app}\{#AppExeName}

; 最低系统要求：Windows 10 1903+（.NET 8 要求）
MinVersion=10.0.18362

; 不在任务栏显示安装进度
ShowLanguageDialog=no
LanguageDetectionMethod=none

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 单文件可执行
Source: "{#PublishDir}\{#AppExeName}";    DestDir: "{app}"; Flags: ignoreversion

; 如果 publish 目录中存在 .pdb，也一并打包（可选）
; Source: "{#PublishDir}\{#AppName}.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}";    Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时保留游戏数据（.minecraft）
; 只删除程序本身写在 AppData\ArclightLauncher 下的配置 / 日志
Type: filesandordirs; Name: "{userappdata}\ArclightLauncher\logs"
; 如果想完整清除配置，取消下一行注释：
; Type: filesandordirs; Name: "{userappdata}\ArclightLauncher"
