; Build: ISCC /DAppVersion=1.2.3 /DSourceDir=<publish dir> installer\RiisSaveEditor.iss
#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#define ServiceName "RiisSaveEditor"

[Setup]
AppId={{6F0B7A52-3C1D-4B8E-9A47-5D2E1C8F9B13}
AppName=Riis Save Editor
AppVersion={#AppVersion}
AppPublisher=RiisDev
AppPublisherURL=https://github.com/RiisDev/RiisSaveEditor
DefaultDirName={localappdata}\RiisSaveEditor
DefaultGroupName=Riis Save Editor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputBaseFilename=RiisSaveEditor-v{#AppVersion}-setup-win-x64
OutputDir=..\dist
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\RiisSaveEditor.exe
VersionInfoVersion={#AppVersion}
WizardStyle=modern

[Tasks]
Name: "service"; Description: "Run as a Windows service that starts with Windows (optional, asks for administrator approval)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "nssm.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Riis Save Editor"; Filename: "{app}\launch.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\RiisSaveEditor.exe"; Flags: runminimized
Name: "{group}\Uninstall Riis Save Editor"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Riis Save Editor"; Filename: "{app}\launch.cmd"; WorkingDir: "{app}"; IconFilename: "{app}\RiisSaveEditor.exe"; Tasks: desktopicon; Flags: runminimized

[UninstallDelete]
; Files the app and installer create at run time are not tracked by Setup.
Type: files; Name: "{app}\launch.cmd"
Type: files; Name: "{app}\appsettings.Production.json"
Type: files; Name: "{app}\service.log*"

[Code]
var
  RemoveCache: Boolean;
  PortPage: TInputQueryWizardPage;
  Port: String;
  PortPrefilled: Boolean;

// Port from the existing config on upgrades, so a reinstall does not reset it.
function ExistingPort(): String;
var
  Text: AnsiString;
  P: Integer;
begin
  Result := '';
  if LoadStringFromFile(ExpandConstant('{app}\appsettings.Production.json'), Text) then
  begin
    P := Pos('127.0.0.1:', Text);
    if P > 0 then
    begin
      P := P + 10;
      while (P <= Length(Text)) and (Text[P] >= '0') and (Text[P] <= '9') do
      begin
        Result := Result + Text[P];
        P := P + 1;
      end;
    end;
  end;
end;

function ValidPort(const S: String): Boolean;
var
  N: Integer;
begin
  N := StrToIntDef(S, 0);
  Result := (N >= 1) and (N <= 65535);
end;

function ServiceExists(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

// Service changes need administrator rights, so they run as one elevated batch (one UAC prompt).
function RunElevated(const Commands: String): Boolean;
var
  Batch: String;
  Code: Integer;
begin
  Batch := ExpandConstant('{tmp}\riis-service.cmd');
  Result := SaveStringToFile(Batch, '@echo off' + #13#10 + Commands, False) and
    ShellExec('runas', ExpandConstant('{cmd}'), '/c "' + Batch + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) and
    (Code = 0);
end;

function StopServiceCommands(): String;
begin
  Result := '"' + ExpandConstant('{app}\nssm.exe') + '" stop {#ServiceName} >nul 2>&1' + #13#10;
end;

function InstallServiceCommands(): String;
var
  Nssm, Exe: String;
begin
  Nssm := '"' + ExpandConstant('{app}\nssm.exe') + '"';
  Exe := '"' + ExpandConstant('{app}\RiisSaveEditor.exe') + '"';
  Result :=
    Nssm + ' stop {#ServiceName} >nul 2>&1' + #13#10 +
    Nssm + ' remove {#ServiceName} confirm >nul 2>&1' + #13#10 +
    Nssm + ' install {#ServiceName} ' + Exe + #13#10 +
    Nssm + ' set {#ServiceName} AppDirectory "' + ExpandConstant('{app}') + '"' + #13#10 +
    Nssm + ' set {#ServiceName} DisplayName "Riis Save Editor"' + #13#10 +
    Nssm + ' set {#ServiceName} Description "Self-hosted save editor on http://localhost:' + Port + '"' + #13#10 +
    Nssm + ' set {#ServiceName} Start SERVICE_AUTO_START' + #13#10 +
    Nssm + ' set {#ServiceName} AppStdout "' + ExpandConstant('{app}\service.log') + '"' + #13#10 +
    Nssm + ' set {#ServiceName} AppStderr "' + ExpandConstant('{app}\service.log') + '"' + #13#10 +
    Nssm + ' set {#ServiceName} AppRotateFiles 1' + #13#10 +
    Nssm + ' set {#ServiceName} AppRotateBytes 5242880' + #13#10 +
    Nssm + ' start {#ServiceName}' + #13#10;
end;

procedure InitializeWizard();
begin
  PortPage := CreateInputQueryPage(wpSelectDir, 'Port', 'Which port should Riis Save Editor use?',
    'It only listens on this computer (127.0.0.1). Change it if 8080 is already taken.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '8080';
  if ServiceExists() then
    WizardSelectTasks('service');
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Existing: String;
begin
  if (CurPageID = PortPage.ID) and not PortPrefilled then
  begin
    PortPrefilled := True;
    Existing := ExistingPort();
    if ExpandConstant('{param:PORT|}') <> '' then
      PortPage.Values[0] := ExpandConstant('{param:PORT|}')
    else if Existing <> '' then
      PortPage.Values[0] := Existing;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Result := ValidPort(Trim(PortPage.Values[0]));
    if not Result then
      MsgBox('Enter a port between 1 and 65535.', mbError, MB_OK);
  end;
end;

// An upgrade cannot replace the exe while the service holds it.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Silent installs skip the port page, so take the port from /PORT= (or the existing one).
  Port := Trim(PortPage.Values[0]);
  if WizardSilent then
  begin
    if ExpandConstant('{param:PORT|}') <> '' then
      Port := ExpandConstant('{param:PORT|}')
    else if ExistingPort() <> '' then
      Port := ExistingPort();
  end;
  if not ValidPort(Port) then
  begin
    Result := 'Invalid port: ' + Port;
    Exit;
  end;
  if ServiceExists() and FileExists(ExpandConstant('{app}\nssm.exe')) then
    RunElevated(StopServiceCommands());
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\appsettings.Production.json'),
      '{ "Urls": "http://127.0.0.1:' + Port + '" }' + #13#10, False);
    // Start the app unless the service already runs, wait until the server answers, then open the browser.
    SaveStringToFile(ExpandConstant('{app}\launch.cmd'),
      '@echo off' + #13#10 +
      'cd /d "%~dp0"' + #13#10 +
      'sc query {#ServiceName} 2>nul | find "RUNNING" >nul && goto wait' + #13#10 +
      'start "Riis Save Editor" /min RiisSaveEditor.exe' + #13#10 +
      ':wait' + #13#10 +
      'for /l %%i in (1,1,30) do (' + #13#10 +
      '  curl.exe -s -o nul http://127.0.0.1:' + Port + ' && goto open' + #13#10 +
      '  timeout /t 1 /nobreak >nul' + #13#10 +
      ')' + #13#10 +
      ':open' + #13#10 +
      'start http://localhost:' + Port + #13#10, False);
    if WizardIsTaskSelected('service') then
    begin
      if not RunElevated(InstallServiceCommands()) then
        MsgBox('The service was not installed (administrator approval is needed). Riis Save Editor still works from its shortcut.', mbInformation, MB_OK);
    end
    else if ServiceExists() then
      RunElevated('"' + ExpandConstant('{app}\nssm.exe') + '" start {#ServiceName}' + #13#10);

    // [Run] entries execute before this step, when launch.cmd does not exist yet, so launch it here.
    // /LAUNCH=1 lets a silent install do the same for testing.
    if (not WizardSilent) or (ExpandConstant('{param:LAUNCH|}') <> '') then
      ShellExec('open', ExpandConstant('{app}\launch.cmd'), '', ExpandConstant('{app}'), SW_SHOWMINNOACTIVE, ewNoWait, ResultCode);
  end;
end;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

function InitializeUninstall(): Boolean;
var
  F: TForm;
  Box: TNewCheckBox;
  Label1: TNewStaticText;
  Ok, Cancel: TNewButton;
begin
  Result := True;
  RemoveCache := HasParam('/REMOVEDATA');
  if UninstallSilent then
    Exit;

  F := TForm.Create(nil);
  try
    F.Caption := 'Uninstall Riis Save Editor';
    F.ClientWidth := ScaleX(380);
    F.ClientHeight := ScaleY(130);
    F.Position := poScreenCenter;
    F.BorderStyle := bsDialog;

    Label1 := TNewStaticText.Create(F);
    Label1.Parent := F;
    Label1.Left := ScaleX(16);
    Label1.Top := ScaleY(16);
    Label1.Width := ScaleX(348);
    Label1.AutoSize := False;
    Label1.WordWrap := True;
    Label1.Height := ScaleY(32);
    Label1.Caption := 'Riis Save Editor will be removed from this computer.';

    Box := TNewCheckBox.Create(F);
    Box.Parent := F;
    Box.Left := ScaleX(16);
    Box.Top := ScaleY(54);
    Box.Width := ScaleX(348);
    Box.Caption := 'Also delete all saved data and the install folder';

    Ok := TNewButton.Create(F);
    Ok.Parent := F;
    Ok.Caption := 'Uninstall';
    Ok.ModalResult := mrOk;
    Ok.Default := True;
    Ok.Left := ScaleX(208);
    Ok.Top := ScaleY(90);
    Ok.Width := ScaleX(75);
    Ok.Height := ScaleY(23);

    Cancel := TNewButton.Create(F);
    Cancel.Parent := F;
    Cancel.Caption := 'Cancel';
    Cancel.ModalResult := mrCancel;
    Cancel.Cancel := True;
    Cancel.Left := ScaleX(289);
    Cancel.Top := ScaleY(90);
    Cancel.Width := ScaleX(75);
    Cancel.Height := ScaleY(23);

    Result := F.ShowModal = mrOk;
    RemoveCache := RemoveCache or Box.Checked;
  finally
    F.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if ServiceExists() then
      RunElevated(
        '"' + ExpandConstant('{app}\nssm.exe') + '" stop {#ServiceName} >nul 2>&1' + #13#10 +
        '"' + ExpandConstant('{app}\nssm.exe') + '" remove {#ServiceName} confirm' + #13#10);
    // A copy started from the shortcut keeps its exe and data files locked. Match by path so a
    // development build running elsewhere is left alone.
    // The uninstaller is 32-bit; a 32-bit PowerShell cannot read the path of a 64-bit process, so use the native one.
    if not Exec(ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -Command "Get-Process RiisSaveEditor -ErrorAction SilentlyContinue | ' +
      'Where-Object { $_.Path -eq ''' + ExpandConstant('{app}\RiisSaveEditor.exe') + ''' } | Stop-Process -Force"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('Could not run powershell to stop the app');
    Sleep(500);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // Without the checkbox App_Data (keys, known passwords, caches) stays so a reinstall picks it up.
    if RemoveCache then
      DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
