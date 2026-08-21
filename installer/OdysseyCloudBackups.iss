; Inno Setup script for Odyssey Cloud Backups.
; Installs PER-USER (no admin prompt) so the built-in auto-updater can
; replace the exe without elevation. Build via build-installer.ps1.

#define MyAppName "Odyssey Cloud Backups"
#define MyAppExeName "Odyssey Cloud Backups.exe"
#define MyAppPublisher "Odyssey Software"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F1C6A0B-3F5E-4D2B-9C41-8A2E5D7B0C93}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=OdysseyCloudBackupsSetup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
; The exe is fully self-contained (the .NET runtime and all libraries are
; embedded) - it is the only file that needs installing.
Source: "..\bin\Release\net8.0-windows\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the scheduled backup task and the login autostart entry.
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN MariaDBBackupTray"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MariaDBBackupTray /f"; Flags: runhidden; RunOnceId: "DelAutostart"
