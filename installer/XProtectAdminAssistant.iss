; Inno Setup script for XProtect Admin Assistant (https://jrsoftware.org/isinfo.php).
; Built by CI on Windows:  ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<build output> installer\XProtectAdminAssistant.iss
;
; Design:
; - Installs per user by default (no administrator rights needed); "Install for all users" is offered by Inno's
;   own privileges dialog. The application itself never asks for elevation.
; - Does not install or change anything else: MilestonePSTools, PowerShell settings, execution policy, the
;   PowerShell Gallery and package providers are left to the administrator (the setup wizard in the app explains them).
; - Uninstalling removes the program files only. Settings, connection profiles, the audit log and saved passwords
;   (Windows Credential Manager) belong to the user and are kept.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\MilestoneAdmin.UI\bin\Release\net48"
#endif

[Setup]
AppId={{D5991595-38CC-4ACD-B9CF-719D844D4085}
AppName=XProtect Admin Assistant
AppVersion={#AppVersion}
AppVerName=XProtect Admin Assistant {#AppVersion}
AppPublisher=XProtect Admin Assistant contributors
DefaultDirName={autopf}\XProtect Admin Assistant
DefaultGroupName=XProtect Admin Assistant
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=XProtectAdminAssistant-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\XProtectAdminAssistant.exe
UninstallDisplayName=XProtect Admin Assistant

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\XProtect Admin Assistant"; Filename: "{app}\XProtectAdminAssistant.exe"
Name: "{autodesktop}\XProtect Admin Assistant"; Filename: "{app}\XProtectAdminAssistant.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\XProtectAdminAssistant.exe"; Description: "Start XProtect Admin Assistant"; Flags: nowait postinstall skipifsilent

[Code]
// .NET Framework 4.8 is required by the application and its PowerShell worker. Release value 528040 is the lowest
// value for 4.8 (Microsoft: "How to determine which .NET Framework versions are installed").
function IsDotNet48Installed(): Boolean;
var
  release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', release) and (release >= 528040);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet48Installed() then
  begin
    Result := MsgBox('XProtect Admin Assistant requires .NET Framework 4.8, which was not found on this computer.' + #13#10#13#10 +
      'Install .NET Framework 4.8 (or later 4.8.x) from Microsoft first. Continue anyway?', mbConfirmation, MB_YESNO) = IDYES;
  end;
end;
