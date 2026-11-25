#define ProductURL "https://github.com/EEkebin/P5Cast"
#define InstallDir "{app}\versions\" + ReleaseVersion

[Setup]
AppId=EEkebin.P5Cast
AppName=P5Cast
AppVersion={#ReleaseVersion}
AppPublisher=EEkebin
AppPublisherURL={#ProductURL}
AppSupportURL={#ProductURL}/issues
AppUpdatesURL={#ProductURL}/releases
DefaultDirName={localappdata}\Programs\P5Cast
DefaultGroupName=P5Cast
DisableProgramGroupPage=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#PayloadDir}\app\assets\mark.ico
UninstallDisplayIcon={#InstallDir}\P5Cast.exe
VersionInfoVersion={#ReleaseVersion}
VersionInfoDescription=P5Cast Setup
WizardStyle=modern dynamic
Compression=lzma2/fast
SolidCompression=yes
OutputBaseFilename=P5Cast-{#ReleaseVersion}-windows-x64-setup

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{#InstallDir}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\P5Cast"; Filename: "{#InstallDir}\P5Cast.exe"; WorkingDir: "{#InstallDir}"

[Run]
Filename: "{#InstallDir}\P5Cast.exe"; WorkingDir: "{#InstallDir}"; Description: "Open P5Cast"; Flags: nowait postinstall skipifsilent
