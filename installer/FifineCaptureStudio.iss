; FIFINE Capture Studio installer.
; Build with: ISCC.exe /DPublishDir=<path to published folder> installer\FifineCaptureStudio.iss
; The publish folder is produced by tools\publish.ps1.

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\selfcontained"
#endif

#define AppName "FIFINE Capture Studio"
#define AppVersion "1.0.0"
#define AppPublisher "FIFINE Capture Studio"
#define AppExe "FifineCaptureStudio.exe"

[Setup]
AppId={{7C4F5A62-2E52-4C08-9F2B-2F0C1E6A6D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FIFINE Capture Studio
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=FifineCaptureStudio-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only build output is removed. Settings, logs and recordings under %LOCALAPPDATA% and
; the user's Videos folder are intentionally left in place.
Type: filesandordirs; Name: "{app}"
