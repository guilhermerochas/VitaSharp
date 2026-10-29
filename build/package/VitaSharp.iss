; Inno Setup 6 or 7 script for the Windows installer (build\publish.ps1 -Package runs it when ISCC.exe is found).
;   ISCC.exe /DSourceDir=<publish folder> /DOutputDir=<packages folder> /DAppVersion=<version> /DOutputName=<name> VitaSharp.iss
; Installs per machine (Program Files) or per user, adds Start menu / optional desktop shortcuts and an uninstaller.
; User data stays in %APPDATA%\VitaSharp and is not removed on uninstall.
#ifndef SourceDir
  #error SourceDir must be defined
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef OutputName
  #define OutputName "VitaSharp-setup"
#endif

[Setup]
AppId={{6F0C7A3B-4C3E-4E55-9D49-56A1B0E9C2D7}
AppName=VitaSharp
AppVersion={#AppVersion}
AppPublisher=VitaSharp contributors
DefaultDirName={autopf}\VitaSharp
DefaultGroupName=VitaSharp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#SourceDir}\LICENSE
SetupIconFile={#SourcePath}..\..\src\VitaSharp.Desktop\Assets\VitaSharp.ico
UninstallDisplayIcon={app}\VitaSharp.Desktop.exe
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\VitaSharp"; Filename: "{app}\VitaSharp.Desktop.exe"
Name: "{autodesktop}\VitaSharp"; Filename: "{app}\VitaSharp.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VitaSharp.Desktop.exe"; Description: "{cm:LaunchProgram,VitaSharp}"; Flags: nowait postinstall skipifsilent
