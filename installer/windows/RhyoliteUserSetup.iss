; ==============================================================================
; Rhyolite Per-User Inno Setup Script (No Administrator / UAC Required)
; Installs single-file Rhyolite.exe (all .dlls combined) into %LocalAppData%\Programs\Rhyolite
; ==============================================================================

#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif

[Setup]
AppId={{8D4F9B3A-7E21-4C90-A123-9E8F7C6B5A41}
AppName=Rhyolite
AppVersion={#AppVersion}
AppPublisher=Dustin Blad
AppPublisherURL=https://github.com/dustinblad/Rhyolite
DefaultDirName={autopf}\Rhyolite
DefaultGroupName=Rhyolite
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\..\src\Rhyolite.Desktop\Rhyolite.ico
UninstallDisplayIcon={app}\Rhyolite.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=Rhyolite-UserSetup-v{#AppVersion}-win-x64
ChangesAssociations=yes
ChangesEnvironment=yes

[Files]
Source: "..\..\artifacts\releases\v{#AppVersion}\win-x64\Rhyolite.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\src\Rhyolite.Desktop\Rhyolite.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Rhyolite"; Filename: "{app}\Rhyolite.exe"; IconFilename: "{app}\Rhyolite.exe"
Name: "{autodesktop}\Rhyolite"; Filename: "{app}\Rhyolite.exe"; IconFilename: "{app}\Rhyolite.exe"

[Registry]
Root: HKA; Subkey: "Software\Classes\.canvas\OpenWithProgids"; ValueType: string; ValueName: "Rhyolite.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "Rhyolite.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Rhyolite.Document"; ValueType: string; ValueName: ""; ValueData: "Rhyolite Knowledge Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Rhyolite.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Rhyolite.exe,0"
Root: HKA; Subkey: "Software\Classes\Rhyolite.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Rhyolite.exe"" ""%1"""

[Run]
Filename: "{app}\Rhyolite.exe"; Description: "Launch Rhyolite"; Flags: nowait postinstall skipifsilent
