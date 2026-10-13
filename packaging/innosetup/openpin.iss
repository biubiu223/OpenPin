; OpenPin Inno Setup Script
; Builds a complete, self-contained Windows Installer (win-x64)
; MIT License - OpenPin

#define MyAppName "OpenPin"
#define MyAppPublisher "OpenPin"
#define MyAppURL "https://github.com/OpenPin/OpenPin"

#ifndef MyAppVersion
#define MyAppVersion "0.1.1"
#endif

#ifndef MySourceDir
#define MySourceDir "..\..\publish\OpenPin-SelfContained"
#endif

#ifndef MyOutputDir
#define MyOutputDir "..\..\publish\release"
#endif

[Setup]
AppId={{9B73428C-2D91-4560-A1E4-90B75E6D8A21}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\..\LICENSE
OutputDir={#MyOutputDir}
OutputBaseFilename=OpenPin-{#MyAppVersion}-Setup-x64
SetupIconFile=..\..\src\OpenPin\Resources\AppIcon.ico
UninstallDisplayIcon={app}\OpenPin.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=100,100
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
CloseApplicationsFilter=OpenPin.exe

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.AutoStart=开机自动启动 OpenPin
chinesesimplified.LaunchOpenPin=立即启动 OpenPin
english.CreateDesktopIcon=Create a desktop shortcut
english.AutoStart=Launch OpenPin automatically on Windows startup
english.LaunchOpenPin=Launch OpenPin now

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\OpenPin.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\OpenPin.exe"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\OpenPin.exe"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\OpenPin.exe"; Tasks: autostart

[Run]
Filename: "{app}\OpenPin.exe"; Description: "{cm:LaunchOpenPin}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM OpenPin.exe"; Flags: runhidden; RunOnceId: "StopOpenPin"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Ensure running instances are terminated cleanly before copying files
  Exec('taskkill.exe', '/F /IM OpenPin.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
