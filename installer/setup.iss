; Inno Setup script for Valve (ledger 28.08-28.11).
; Silent install: Valve-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="C:\Program Files\Valve" /LOG="valve-install.log"
; Silent uninstall: "C:\Program Files\Valve\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART NL_CLEAN_SETTINGS=1
; Update over existing install: default Inno behavior (files overwritten, configuration preserved under ProgramData)

#define AppName "Valve"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "Valve"
#define AppExeName "Valve.exe"

#ifndef AppDir
  #define AppDir "..\bin\Debug\net8.0-windows"
#endif

[Setup]
AppId={{D1A39E14-41FA-4E0E-B04F-5D9B8E3D7B1C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Valve
DefaultGroupName=Valve
UninstallDisplayName=Valve
OutputBaseFilename=Valve-setup
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
AppMutex=ValveMutex
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#AppDir}\Valve.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\app.manifest"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#AppDir}\WinDivert.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\WinDivert64.sys"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Exclude: "*.pdb;*.tmp"

[Icons]
Name: "{group}\Valve"; Filename: "{app}\Valve.exe"
Name: "{group}\Uninstall Valve"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Valve"; Filename: "{app}\Valve.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\Valve.exe"; Description: "Launch Valve"; Flags: nowait postinstall skipifsilent

[Code]
// Support NL_CLEAN_SETTINGS=1 on uninstall to purge settings and statistics
function ShouldCleanSettings(): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
  begin
    if (CompareText(ParamStr(i), 'NL_CLEAN_SETTINGS=1') = 0) or 
       (CompareText(ParamStr(i), '/NL_CLEAN_SETTINGS=1') = 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  LocalDir: string;
begin
  if (CurUninstallStep = usPostUninstall) and ShouldCleanSettings() then
  begin
    DataDir := ExpandConstant('{commonappdata}\Valve');
    if DirExists(DataDir) then
    begin
      DelTree(DataDir, True, True, True);
    end;

    LocalDir := ExpandConstant('{localappdata}\Valve');
    if DirExists(LocalDir) then
    begin
      DelTree(LocalDir, True, True, True);
    end;

    RegDeleteKeyIncludingSubkeys(HKLM, 'Software\Valve');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Valve');
  end;
end;
