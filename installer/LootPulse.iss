; LootPulse installer (Inno Setup) — framework-dependent, per-user, version-aware upgrades.
;
; Build via installer\build-installer.ps1, which publishes the app to a staging folder and passes
; its path in as /DStagingDir=...  The app version is read straight from the built exe, so the
; csproj <Version> stays the single source of truth.

#define MyAppName "LootPulse"
#define MyAppPublisher "Light-Passion"
#define MyAppExeName "LootPulse.exe"
#define MyAppMutex "LootPulse.Overlay.SingleInstance"
#define MyAppGuid "B8C4B2A1-7F3E-4D52-9A6C-1E2F3A4B5C6D"

#ifndef StagingDir
  #define StagingDir "staging"
#endif

#define MyAppVersion GetVersionNumbersString(AddBackslash(StagingDir) + MyAppExeName)

[Setup]
AppId={{{#MyAppGuid}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
; Detect a running instance and ask to close it before installing/upgrading.
AppMutex={#MyAppMutex}
CloseApplications=yes
RestartApplications=no
; Per-user install by default (no UAC); allow elevating to all-users from the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=LootPulse-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AddBackslash(StagingDir)}*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetRuntimeUrl = 'https://dotnet.microsoft.com/download/dotnet/9.0/runtime';

{ Compare dotted numeric version strings. Returns 1 if A>B, -1 if A<B, 0 if equal. }
function CompareVersion(A, B: String): Integer;
var
  pa, pb, na, nb: Integer;
begin
  Result := 0;
  while ((A <> '') or (B <> '')) and (Result = 0) do
  begin
    pa := Pos('.', A);
    if pa = 0 then begin na := StrToIntDef(A, 0); A := ''; end
    else begin na := StrToIntDef(Copy(A, 1, pa - 1), 0); A := Copy(A, pa + 1, Length(A)); end;

    pb := Pos('.', B);
    if pb = 0 then begin nb := StrToIntDef(B, 0); B := ''; end
    else begin nb := StrToIntDef(Copy(B, 1, pb - 1), 0); B := Copy(B, pb + 1, Length(B)); end;

    if na > nb then Result := 1
    else if na < nb then Result := -1;
  end;
end;

{ Version of any currently-installed LootPulse (recorded under the AppId _is1 uninstall key), or empty. }
function GetInstalledVersion(): String;
var
  v: String;
begin
  Result := '';
  if RegQueryStringValue(HKA,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#MyAppGuid}' + '}_is1',
       'DisplayVersion', v) then
    Result := v;
end;

{ True if a .NET 9.x Windows Desktop runtime is physically installed. }
function IsDotNet9DesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(BasePath + '\9.*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
var
  Installed: String;
  ErrCode: Integer;
begin
  Result := True;

  { Downgrade protection: never let an older build overwrite a newer one. }
  Installed := GetInstalledVersion();
  if (Installed <> '') and (CompareVersion(Installed, '{#MyAppVersion}') > 0) then
  begin
    if not WizardSilent() then
      MsgBox('A newer version of {#MyAppName} (' + Installed + ') is already installed.' + #13#10 +
             'Setup will now exit.', mbInformation, MB_OK);
    Result := False;
    Exit;
  end;

  { Framework-dependent build needs the .NET 9 Desktop Runtime. }
  if not IsDotNet9DesktopInstalled() then
  begin
    if not WizardSilent() then
      if MsgBox('{#MyAppName} requires the .NET 9 Desktop Runtime (x64), which was not found.' + #13#10 +
                'Open the download page now?', mbConfirmation, MB_YESNO) = IDYES then
        ShellExecAsOriginalUser('open', DotNetRuntimeUrl, '', '', SW_SHOW, ewNoWait, ErrCode);
    Result := False;
  end;
end;
