; BTR Library installer (Inno Setup 6). Built by scripts\release.ps1, which passes:
;   /DAppVersion=0.3.0  /DSourceDir=<repo>\src\bin\Release\net48  /DOutputDir=<repo>\dist
; Students just run the resulting BTR-Library-Setup-<version>.exe.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\bin\Release\net48"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Never change AppId: it's how upgrades and the uninstaller find the existing install.
AppId={{8C2B4E61-3F7A-4D5B-9C1E-2A6F0B7D4E93}
AppName=BTR Library
AppVersion={#AppVersion}
AppVerName=BTR Library {#AppVersion}
AppPublisher=Butler Tech Robotics
AppPublisherURL=https://github.com/ButlerTechRobotics/btr-library
AppSupportURL=https://github.com/ButlerTechRobotics/btr-library/issues
AppUpdatesURL=https://github.com/ButlerTechRobotics/btr-library/releases
DefaultDirName={autopf}\BTR Library
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=BTR-Library-Setup-{#AppVersion}
SetupIconFile=..\src\Assets\icon.ico
UninstallDisplayIcon={app}\icon.ico
UninstallDisplayName=BTR Library for SOLIDWORKS
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
VersionInfoProductName=BTR Library for SOLIDWORKS

[Files]
Source: "{#SourceDir}\BtrLibrary.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SolidWorks.Interop.*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\library.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\previews\*.mesh"; DestDir: "{app}\previews"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\src\Assets\icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Previews are named by content; drop ones from older versions.
Type: files; Name: "{app}\previews\*.mesh"

[Run]
; Same registration install.bat does: COM + SOLIDWORKS add-in keys (see SwAddin.Register).
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/codebase ""{app}\BtrLibrary.dll"""; \
  StatusMsg: "Registering the add-in with SOLIDWORKS..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/u ""{app}\BtrLibrary.dll"""; \
  RunOnceId: "UnregisterBtrLibrary"; Flags: runhidden waituntilterminated

[Messages]
FinishedLabel=BTR Library is installed. Start SOLIDWORKS and open the BTR tab in the Task Pane on the right.%n%nIf the tab isn't there, turn on BTR Library under Tools > Add-Ins.

[Code]
// SOLIDWORKS locks BtrLibrary.dll while it's running, so it has to be closed first.
function SolidWorksRunning(): Boolean;
var
  Locator, Service, Procs: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''SLDWORKS.exe''');
    Result := Procs.Count > 0;
  except
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  while SolidWorksRunning() do
  begin
    if MsgBox('Please close SOLIDWORKS before installing BTR Library.' + #13#10#13#10 +
              'Click OK once it is closed, or Cancel to quit.', mbInformation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  while SolidWorksRunning() do
  begin
    if MsgBox('Please close SOLIDWORKS before uninstalling BTR Library.', mbInformation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  end;
end;
