; DeskPane lightweight offline x64 installer (Inno Setup 6)
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\release-out\DeskPane"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "DeskPane-Setup-v0.1.0-win-x64"
#endif

[Setup]
AppId={{8F6C4E2A-9B31-4E5D-A8D2-7C1E90B4F3A2}
AppName=DeskPane
AppVersion={#MyAppVersion}
AppPublisher=DeskPane
DefaultDirName={localappdata}\Programs\DeskPane
DefaultGroupName=DeskPane
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\DeskPane\Assets\deskpane.ico
UninstallDisplayIcon={app}\DeskPane.exe
OutputDir=..
OutputBaseFilename={#OutputBaseName}
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut / 创建桌面快捷方式"; GroupDescription: "Additional tasks / 附加选项:"
Name: "autostart"; Description: "Start DeskPane with Windows / 开机自动启动"; GroupDescription: "Additional tasks / 附加选项:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DeskPane"; Filename: "{app}\DeskPane.exe"
Name: "{group}\恢复 DeskPane 桌面文件"; Filename: "{app}\一键恢复桌面文件.bat"
Name: "{group}\Uninstall DeskPane"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DeskPane"; Filename: "{app}\DeskPane.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeskPane"; ValueData: """{app}\DeskPane.exe"" --startup"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\DeskPane.exe"; Description: "Launch DeskPane / 启动 DeskPane"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Restore any staged desktop files before uninstalling so user files are never left behind.
Filename: "{app}\DeskPane.exe"; Parameters: "--restore-desktop"; Flags: runhidden; RunOnceId: "RestoreDesktopFiles"
