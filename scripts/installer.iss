[Setup]
AppId={{B9B850C9-10D5-427D-AEAA-6E4C91DA16DD}
AppName=Куда делась память?
AppVersion=0.1.0
DefaultDirName={localappdata}\Programs\KudaDelasPamyat
DefaultGroupName=Куда делась память
UninstallDisplayIcon={app}\DiskSpace.exe
OutputBaseFilename=KudaDelasPamyat-Setup-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
MinVersion=10.0.22000
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Куда делась память"; Filename: "{app}\DiskSpace.exe"
Name: "{group}\Демонстрационный режим"; Filename: "{app}\DiskSpace.exe"; Parameters: "--demo"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "KudaDelasPamyat"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\DiskSpace.exe"; Description: "Открыть приложение"; Flags: nowait postinstall skipifsilent
