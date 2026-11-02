; HangAll installer. Built with Inno Setup 7:
;   ISCC.exe installer\hangall.iss /DVersion=1.0.0 /DSource=..\dist\win-x64
; Packages the framework-dependent build and checks for the .NET 8 Desktop Runtime.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\dist\win-x64"
#endif

[Setup]
AppId={{B459D277-A174-4E0F-B572-FCA78270C3E2}
AppName=HangAll
AppVersion={#Version}
AppVerName=HangAll {#Version}
AppPublisher=WatsonLearnCode
VersionInfoVersion={#Version}
VersionInfoProductVersion={#Version}
VersionInfoProductName=HangAll
VersionInfoDescription=HangAll Setup
VersionInfoCompany=WatsonLearnCode
VersionInfoCopyright=Copyright (c) 2026 HangAll contributors
AppPublisherURL=https://github.com/WatsonLearnCode/HangAll
AppSupportURL=https://github.com/WatsonLearnCode/HangAll/issues
AppUpdatesURL=https://github.com/WatsonLearnCode/HangAll/releases
DefaultDirName={localappdata}\Programs\HangAll
DefaultGroupName=HangAll
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=HangAll-{#Version}-Setup
SetupIconFile=..\src\HangAll\Assets\HangAll.ico
UninstallDisplayIcon={app}\HangAll.exe
UninstallDisplayName=HangAll
WizardStyle=modern
WizardSmallImageFile=wizard-small.bmp
WizardImageFile=wizard.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763
AppMutex=Local\HangAll.V1.SingleInstance

[Languages]
; The four translations ship with the repository: Inno Setup 6 does not bundle
; the Chinese ones, and Inno Setup 7 does, so a local copy keeps both builds equal.
Name: "english"; MessagesFile: "compiler:Default.isl"; LicenseFile: "..\LICENSE"
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"; LicenseFile: "..\licenses\LICENSE.zh-CN.txt"
Name: "chinesetraditional"; MessagesFile: "languages\ChineseTraditional.isl"; LicenseFile: "..\licenses\LICENSE.zh-Hant.txt"
Name: "japanese"; MessagesFile: "languages\Japanese.isl"; LicenseFile: "..\licenses\LICENSE.ja-JP.txt"
Name: "korean"; MessagesFile: "languages\Korean.isl"; LicenseFile: "..\licenses\LICENSE.ko-KR.txt"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start HangAll when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#Source}\HangAll.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\HangAll\Assets\Fonts\OFL.txt"; DestDir: "{app}"; DestName: "Manrope-OFL.txt"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\licenses\LICENSE.zh-TW.txt"

[Icons]
Name: "{group}\HangAll"; Filename: "{app}\HangAll.exe"; IconFilename: "{app}\HangAll.exe"; IconIndex: 0; AppUserModelID: "HangAll.Desktop.V1"
Name: "{group}\Uninstall HangAll"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HangAll"; Filename: "{app}\HangAll.exe"; IconFilename: "{app}\HangAll.exe"; IconIndex: 0; Tasks: desktopicon; AppUserModelID: "HangAll.Desktop.V1"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HangAll"; ValueData: """{app}\HangAll.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\HangAll.exe"; Description: "{cm:LaunchProgram,HangAll}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM HangAll.exe /F"; Flags: runhidden; RunOnceId: "StopHangAll"

[Code]
function HasWindowsDesktopRuntimeAt(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if Root = '' then
    exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\8.*', FindRec) then
  begin
    try
      Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
    finally
      FindClose(FindRec);
    end;
  end;
end;

function HasDotNet8DesktopRuntime(): Boolean;
var
  names: TArrayOfString;
  i: Integer;
begin
  Result := False;
  if RegGetValueNames(
       HKLM,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
       names) then
  begin
    for i := 0 to GetArrayLength(names) - 1 do
      if Pos('8.', names[i]) = 1 then
      begin
        Result := True;
        exit;
      end;
  end;
  if RegGetValueNames(
       HKCU,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
       names) then
  begin
    for i := 0 to GetArrayLength(names) - 1 do
      if Pos('8.', names[i]) = 1 then
      begin
        Result := True;
        exit;
      end;
  end;
  if HasWindowsDesktopRuntimeAt(AddBackslash(GetEnv('USERPROFILE')) + '.dotnet') or
     HasWindowsDesktopRuntimeAt(ExpandConstant('{localappdata}\.dotnet')) or
     HasWindowsDesktopRuntimeAt(GetEnv('DOTNET_ROOT')) or
     HasWindowsDesktopRuntimeAt(ExpandConstant('{pf}\dotnet')) then
  begin
    Result := True;
    exit;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not HasDotNet8DesktopRuntime() then
  begin
    SuppressibleMsgBox(
      'HangAll requires the Microsoft .NET 8 Desktop Runtime (x64).' + #13#10 +
      'Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run Setup again.',
      mbCriticalError,
      MB_OK,
      IDOK);
    Result := False;
  end;
end;

// Stop a running copy before the files are replaced, so the install never asks for a restart.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM HangAll.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// The app's own Settings can turn "Start with Windows" on after installation.
// Uninstalling removes that entry whether Setup or the app wrote it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'HangAll');
  end;
end;
