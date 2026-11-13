param(
    [Parameter(Mandatory)][string]$OldExe,
    [Parameter(Mandatory)][string]$NewExe,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$TransientTargetRead
)

$ErrorActionPreference = 'Stop'
$previous = Get-Item -LiteralPath $OldExe
$candidate = Get-Item -LiteralPath $NewExe
$previousVersion = $previous.VersionInfo.ProductVersion.Split('+')[0]
$newVersion = $candidate.VersionInfo.ProductVersion.Split('+')[0]
if ([version]$newVersion -le [version]$previousVersion) { throw 'The candidate must be newer.' }

$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a new evidence directory.' }
$install = Join-Path $evidence 'installed-copy'
$profile = Join-Path $evidence 'profile'
$stageId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $profile "updates\stage-$stageId"
[IO.Directory]::CreateDirectory($install) | Out-Null
[IO.Directory]::CreateDirectory($stage) | Out-Null
$target = Join-Path $install 'PressReleaseWriter.exe'
$payload = Join-Path $stage 'PressReleaseWriter.exe'
Copy-Item -LiteralPath $previous.FullName -Destination $target
Copy-Item -LiteralPath $candidate.FullName -Destination $payload
$originalHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$newHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash

function Wait-ReadyWindow($Process, [string]$Version) {
    $limit = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
    } while (-not $Process.HasExited -and $Process.MainWindowTitle -notlike "*$Version*" -and [DateTime]::UtcNow -lt $limit)
    if ($Process.HasExited -or $Process.MainWindowTitle -notlike "*$Version*" -or -not $Process.Responding) {
        throw "App did not open a responsive v$Version window."
    }
}

$appArgs = @('--data-dir', ('"' + $profile + '"'))
$oldApp = Start-Process -FilePath $target -ArgumentList $appArgs -WindowStyle Hidden -PassThru
$updatedApp = $null
$readyEvent = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, "Local\PressReleaseWriter.Update.$stageId")
try {
    Wait-ReadyWindow $oldApp $previousVersion
    $helperArgs = @('--apply-update', ('"' + $target + '"'), [string]$oldApp.Id,
        [string]$oldApp.StartTime.ToUniversalTime().Ticks, $newHash, $newVersion, ('"' + $profile + '"'))
    $imageLock = [IO.File]::Open($payload, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $helper = Start-Process -FilePath $payload -ArgumentList $helperArgs -WindowStyle Hidden -PassThru
        if (-not $readyEvent.WaitOne(20000)) {
            if ($helper.HasExited) { throw "Update helper exited before readiness: $($helper.ExitCode)" }
            throw 'Update helper did not validate the running app.'
        }
    }
    finally { $imageLock.Dispose() }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $originalHash) { throw 'Updater replaced a running app.' }
    $targetRead = if ($TransientTargetRead) { [IO.File]::OpenRead($target) }
    try {
        if (-not $oldApp.CloseMainWindow() -or -not $oldApp.WaitForExit(12000)) { throw 'The isolated original test window did not close.' }
        # Keep the old image open for reading while the helper attempts the replace.
        if ($targetRead -and $helper.WaitForExit(8000)) {
            throw "Update helper exited while a temporary reader held the old image: $($helper.ExitCode)"
        }
    }
    finally { if ($targetRead) { $targetRead.Dispose() } }
    if (-not $helper.WaitForExit(55000) -or $helper.ExitCode -ne 0) { throw 'Update helper did not complete successfully.' }

    $backups = @(Get-ChildItem -LiteralPath $install -File -Filter '*.previous-*.bak')
    $actual = Get-FileHash -LiteralPath $target -Algorithm SHA256
    if ($actual.Hash -ne $newHash -or $backups.Count -ne 1 -or
        (Get-FileHash -LiteralPath $backups[0].FullName -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'Updated file or backup did not match its expected hash.'
    }
    $updatedApp = Start-Process -FilePath $target -ArgumentList $appArgs -WindowStyle Hidden -PassThru
    Wait-ReadyWindow $updatedApp $newVersion
    $result = [pscustomobject]@{ OldVersion=$previousVersion; NewVersion=$newVersion; OldHash=$originalHash; TransientTargetRead=[bool]$TransientTargetRead;
        NewHash=$newHash; UpdatedWindow=$updatedApp.MainWindowTitle; Target=$target; Backup=$backups[0].FullName; Passed=$true }
    $json = $result | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $evidence 'update-result.json'), $json, [Text.UTF8Encoding]::new($false))
    $json
}
finally {
    $readyEvent.Dispose()
    foreach ($testApp in @($updatedApp, $oldApp)) {
        if ($null -ne $testApp -and -not $testApp.HasExited) {
            $testApp.CloseMainWindow() | Out-Null
            $testApp.WaitForExit(8000) | Out-Null
        }
    }
}
