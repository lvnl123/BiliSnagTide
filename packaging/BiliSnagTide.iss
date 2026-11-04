#define MyAppName "BiliSnagTide"
#define MyAppVersion "4.5.3"
#define MyAppExeName "BiliSnagTide.exe"

[Setup]
AppId={{83E6AA03-B706-531F-B343-A42A85BEFD2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=lvnl123
AppPublisherURL=https://github.com/lvnl123/BiliSnagTide
AppSupportURL=https://github.com/lvnl123/BiliSnagTide/issues
DefaultDirName={localappdata}\Programs\BiliSnagTide
DefaultGroupName=BiliSnagTide
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\Source\App.ico
OutputDir=..\out\installer
OutputBaseFilename=BiliSnagTide-v4.5.3-Windows-x64-Setup
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
VersionInfoVersion=4.5.3.0
VersionInfoDescription=BiliSnagTide Windows Installer
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\BiliSnagTide"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\BiliSnagTide"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,BiliSnagTide}"; Flags: nowait postinstall skipifsilent
