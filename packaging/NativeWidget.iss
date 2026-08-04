#define MyAppName "Native Widget"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Pelag Michael"
#define MyAppExeName "NativeWidget.exe"

[Setup]
AppId={{D710E863-EFD4-44D8-9244-84F4F758B0A5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Pelag-Michael/Native-Widget
DefaultDirName={localappdata}\Programs\Native Widget
DefaultGroupName=Native Widget
OutputDir=..\dist
OutputBaseFilename=Native-Widget-Setup-v{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\NativeWidget\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE

[Files]
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Native Widget"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Native Widget"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Native Widget"; Flags: nowait postinstall skipifsilent
