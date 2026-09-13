#define AppVersion GetEnv("DESKLOG_APP_VERSION")
#define OutputBaseFilename GetEnv("DESKLOG_OUTPUT_BASE")
#define PayloadRoot GetEnv("DESKLOG_PAYLOAD")

[Setup]
AppId={{7A0C6CF8-12A6-4AC3-B3B6-79F9E66F3D4A}
AppName=DeskLog
AppVersion={#AppVersion}
AppPublisher=DeskLog contributors
DefaultDirName={localappdata}\Programs\Riji
DefaultGroupName=DeskLog
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#PayloadRoot}\riji.ico
UninstallDisplayIcon={app}\DeskLog.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
Uninstallable=yes

[Files]
Source: "{#PayloadRoot}\DeskLog.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\production-default"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\riji.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\Web\*"; DestDir: "{app}\Web"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\extensions\chrome-edge\*"; DestDir: "{app}\extensions\chrome-edge"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\extensions\firefox\*"; DestDir: "{app}\extensions\firefox"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\ThirdParty\*"; DestDir: "{app}\ThirdParty"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\version-*"
Type: files; Name: "{app}\Riji.Desktop.exe"
Type: files; Name: "{app}\Riji.Desktop.dll"
Type: files; Name: "{app}\日迹.lnk"

[Icons]
Name: "{autoprograms}\DeskLog"; Filename: "{app}\DeskLog.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\DeskLog"; Filename: "{app}\DeskLog.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\DeskLog.exe"; Description: "启动 DeskLog"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Riji"; ValueData: "{app}\DeskLog.exe"; Flags: uninsdeletevalue; Check: ShouldRestoreAutostart

[Code]
var
  ExistingAutostart: Boolean;

function InitializeSetup(): Boolean;
var
  Value: String;
begin
  ExistingAutostart := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Riji', Value);
  Result := True;
end;

function ShouldRestoreAutostart(): Boolean;
begin
  Result := ExistingAutostart;
end;
