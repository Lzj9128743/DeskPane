[Code]
const
  DeskPaneAppCompatLayersKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers';

function PrepareDeskPaneDependencies(var NeedsRestart: Boolean): string; forward;

#if DeskPaneBundledRuntime
function QuoteDeskPaneCleanupArgument(Value: string): string;
begin
  Result := '"' + Value + '"';
end;

procedure CleanupDeskPaneInstall;
var
  PowerShellPath: string;
  CleanupScriptPath: string;
  CurrentManifestPath: string;
  LegacyManifestPath: string;
  PreviousManifestPath: string;
  Parameters: string;
  ResultCode: Integer;
begin
  if not DirectInstallUpgrade then
  begin
    Log('DeskPane first-install path detected; stale install cleanup skipped.');
    Exit;
  end;

  ExtractTemporaryFile('cleanup-deskpane-install.ps1');
  ExtractTemporaryFile('DeskPane.LegacyBundledRuntimeFiles.txt');

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  CleanupScriptPath := ExpandConstant('{tmp}\cleanup-deskpane-install.ps1');
  CurrentManifestPath := ExpandConstant('{tmp}\DeskPane.InstallManifest.current.txt');
  LegacyManifestPath := ExpandConstant('{tmp}\DeskPane.LegacyBundledRuntimeFiles.txt');
  PreviousManifestPath := AddBackslash(WizardDirValue) + 'DeskPane.InstallManifest.txt';
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    QuoteDeskPaneCleanupArgument(CleanupScriptPath) +
    ' -InstallRoot ' + QuoteDeskPaneCleanupArgument(WizardDirValue) +
    ' -CurrentManifestPath ' + QuoteDeskPaneCleanupArgument(CurrentManifestPath) +
    ' -LegacyManifestPath ' + QuoteDeskPaneCleanupArgument(LegacyManifestPath) +
    ' -PreviousManifestPath ' + QuoteDeskPaneCleanupArgument(PreviousManifestPath);

  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('DeskPane could not start the install cleanup process.');

  if ResultCode <> 0 then
    RaiseException(Format('DeskPane install cleanup failed with exit code %d.', [ResultCode]));

  Log('DeskPane stale install-file cleanup completed successfully.');
end;
#endif

procedure DeleteAppCompatLayerValue(RootKey: Integer; ExePath: string);
var
  Value: string;
begin
  if ExePath = '' then
    Exit;

  if RegQueryStringValue(RootKey, DeskPaneAppCompatLayersKey, ExePath, Value) and
     (Pos('RUNASADMIN', Uppercase(Value)) > 0) then
  begin
    if RegDeleteValue(RootKey, DeskPaneAppCompatLayersKey, ExePath) then
      Log('DeskPane installer removed AppCompat RUNASADMIN value: ' + ExePath)
    else
      Log('DeskPane installer could not remove AppCompat value: ' + ExePath);
  end;
end;

procedure CleanupInstallAppCompatFlags(InstallPath: string);
var
  ExePath: string;
begin
  ExePath := AddBackslash(NormalizeDirPath(InstallPath)) + DeskPaneLegacyExeName;
  DeleteAppCompatLayerValue(HKEY_CURRENT_USER, ExePath);

  if IsAdminInstallMode then
    DeleteAppCompatLayerValue(HKEY_LOCAL_MACHINE, ExePath);
end;

function InitializeSetup: Boolean;
begin
  // Program Files is now the supported all-users location. Older installations
  // found there are upgraded in place instead of being treated as disposable
  // legacy copies.
  Result := PrepareDirectInstallPlan;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := DirectInstallUpgrade and (PageID = wpSelectDir);
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  DependencyError: string;
begin
  Result := '';

  // Close only the DeskPane process that belongs to the install being updated.
  // Restart Manager remains the final file-lock fallback for Setup itself.
  if not StopDeskPaneProcessesAtPath(WizardDirValue) then
    Log('DeskPane path-scoped process shutdown failed; Setup will continue with Restart Manager handling file locks.');

  // Give the process time to fully exit before Restart Manager runs —
  // only meaningful when an existing install was just asked to close; a
  // first install has nothing to wait for.
  if DirectInstallUpgrade then
    Sleep(2000);
  Log('DeskPane process termination completed.');

  // DeskPane must continue to run at normal user integrity so Explorer drag and
  // drop remains available after an upgrade from older Program Files builds.
  CleanupInstallAppCompatFlags(WizardDirValue);

  DependencyError := PrepareDeskPaneDependencies(NeedsRestart);
  if DependencyError <> '' then
    Result := DependencyError;
end;
