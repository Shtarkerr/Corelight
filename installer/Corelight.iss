; Сборка через scripts/Build-Packages.ps1. Данные в ProgramData не принадлежат установщику.
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Packages.ps1
#endif
#ifndef BinaryVersion
  #error BinaryVersion must be supplied by Build-Packages.ps1
#endif
#ifndef ProductTextVersion
  #error ProductTextVersion must be supplied by Build-Packages.ps1
#endif
#ifndef SourceDir
  #error SourceDir must point to the published application
#endif
#ifndef PackageDir
  #error PackageDir must point to the output directory
#endif
#ifndef PackageName
  #error PackageName must be supplied by Build-Packages.ps1
#endif

[Setup]
AppId={{13A96558-3607-43CD-AFF0-A6E1D86712FC}
AppName=Corelight
AppVersion={#AppVersion}
VersionInfoVersion={#BinaryVersion}
VersionInfoProductTextVersion={#ProductTextVersion}
AppPublisher=Shtarkerr
AppPublisherURL=https://github.com/Shtarkerr/Corelight
AppSupportURL=https://github.com/Shtarkerr/Corelight/issues
AppUpdatesURL=https://github.com/Shtarkerr/Corelight/releases
DefaultDirName={autopf}\Corelight
DefaultGroupName=Corelight
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern
SetupIconFile=Corelight.ico
LicenseFile=..\LICENSE
InfoBeforeFile=BeforeInstall.txt
OutputDir={#PackageDir}
OutputBaseFilename={#PackageName}
Compression=lzma2/fast
SolidCompression=yes
UninstallDisplayIcon={app}\Corelight.exe
AppMutex=Global\Corelight.SingleInstance
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Corelight.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Corelight"; Filename: "{app}\Corelight.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Corelight"; Filename: "{app}\Corelight.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Corelight.exe"; Description: "Запустить Corelight"; Flags: postinstall runascurrentuser nowait skipifsilent unchecked

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('Global\Corelight.SingleInstance');
  if not Result then
    MsgBox('Закройте Corelight перед удалением программы.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('Программа удалена. Применённые настройки Windows, история и резервные копии сохранены. Для отката установите Corelight снова и откройте «История и откат».', mbInformation, MB_OK);
end;
