#!/usr/bin/env pwsh

# AITestCrew Agent Installer

#

# Usage:

#   .\install.ps1 [-InstallPath <path>] [-CleanReinstall]

# Default install path: C:\Tools\AITestCrew



param(

    [string]$InstallPath = 'C:\Tools\AITestCrew',

    [switch]$CleanReinstall

)



$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot



function Write-Header([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Write-Ok([string]$msg)     { Write-Host "  $msg" -ForegroundColor Green }

function Write-Info([string]$msg)   { Write-Host "  $msg" }

function Write-Warn([string]$msg)   { Write-Host "  WARNING: $msg" -ForegroundColor Yellow }



function Get-BuildLabel([string]$folder) {

    $f = Join-Path $folder 'build-info.json'

    if (-not (Test-Path $f)) { return 'unknown' }

    try {

        $b  = Get-Content $f -Raw | ConvertFrom-Json

        $dt = if ($b.buildDate -and $b.buildDate.Length -ge 10) { $b.buildDate.Substring(0,10) } else { '' }

        return "v$($b.version) ($dt)"

    } catch { return 'unknown' }

}



# ConvertFrom-Json -AsHashtable is PS6+. Recurse manually so we work on Windows PowerShell 5.1.

function ConvertTo-Hashtable($InputObject) {

    if ($null -eq $InputObject) { return $null }

    if ($InputObject -is [System.Collections.IDictionary]) {

        $ht = @{}

        foreach ($key in @($InputObject.Keys)) { $ht[[string]$key] = ConvertTo-Hashtable $InputObject[$key] }

        return $ht

    }

    if ($InputObject -is [System.Management.Automation.PSCustomObject]) {

        $ht = @{}

        foreach ($prop in $InputObject.PSObject.Properties) { $ht[$prop.Name] = ConvertTo-Hashtable $prop.Value }

        return $ht

    }

    if ($InputObject -is [System.Collections.IEnumerable] -and $InputObject -isnot [string]) {

        return ,@(foreach ($item in $InputObject) { ConvertTo-Hashtable $item })

    }

    return $InputObject

}



# Merge-AppSettings: merges QA existing config on top of incoming admin layout.

# Returns: @{ Merged, Preserved(List[string]), Removed(List[string]) }

function Merge-AppSettings {

    param([hashtable]$Incoming, [hashtable]$Existing)

    $preserved = [System.Collections.Generic.List[string]]::new()

    $removed   = [System.Collections.Generic.List[string]]::new()

    $result    = ConvertTo-Hashtable ($Incoming | ConvertTo-Json -Depth 100 | ConvertFrom-Json)

    if (-not $result.ContainsKey('TestEnvironment') -or

        -not $Existing.ContainsKey('TestEnvironment')) {

        return @{ Merged = $result; Preserved = $preserved; Removed = $removed }

    }

    $rTe = $result['TestEnvironment']

    $eTe = $Existing['TestEnvironment']

    foreach ($k in @('ApiKey', 'AgentName')) {

        if ($eTe.ContainsKey($k) -and $null -ne $eTe[$k] -and "$($eTe[$k])" -ne '') {

            if ($rTe.ContainsKey($k)) { $rTe[$k] = $eTe[$k]; $preserved.Add("TestEnvironment.$k") }

            else { $removed.Add("TestEnvironment.$k") }

        }

    }

    if ($eTe.ContainsKey('ServerUrl') -and "$($eTe['ServerUrl'])" -ne '') {

        if ($rTe.ContainsKey('ServerUrl') -and "$($rTe['ServerUrl'])" -eq '') {

            $rTe['ServerUrl'] = $eTe['ServerUrl']

            $preserved.Add('TestEnvironment.ServerUrl')

        }

    }

    if ($eTe.ContainsKey('Environments') -and $eTe['Environments'] -is [hashtable] -and

        $rTe.ContainsKey('Environments') -and $rTe['Environments'] -is [hashtable]) {

        foreach ($envKey in @($eTe['Environments'].Keys)) {

            $eEnv = $eTe['Environments'][$envKey]

            if ($eEnv -isnot [hashtable]) { continue }

            if (-not $rTe['Environments'].ContainsKey($envKey)) {

                foreach ($fk in @($eEnv.Keys)) {

                    if (Test-IsPreservedEnvField $fk $eEnv[$fk]) { $removed.Add("TestEnvironment.Environments.$envKey.$fk") }

                }

                continue

            }

            $rEnv = $rTe['Environments'][$envKey]

            if ($rEnv -isnot [hashtable]) { continue }

            foreach ($fk in @($eEnv.Keys)) {

                if (-not (Test-IsPreservedEnvField $fk $eEnv[$fk])) { continue }

                if ($rEnv.ContainsKey($fk)) { $rEnv[$fk] = $eEnv[$fk]; $preserved.Add("TestEnvironment.Environments.$envKey.$fk") }

                else { $removed.Add("TestEnvironment.Environments.$envKey.$fk") }

            }

        }

    }

    return @{ Merged = $result; Preserved = $preserved; Removed = $removed }

}



function Test-IsPreservedEnvField([string]$fieldName, $value) {

    if ($null -eq $value -or "$value" -eq '') { return $false }

    if ($fieldName -eq 'WinFormsAppPath') { return $true }

    if ($fieldName -match 'Path$') { return $true }

    return $false

}



# Discovers every Playwright storage-state file path referenced by appsettings.json
# (top-level TestEnvironment + per-env). The shipped defaults are bare filenames at
# install root (e.g. 'bravecloud-auth-state.json') -- they live outside the auth-state/
# folder and would otherwise be wiped on upgrade.

function Get-StorageStatePaths([hashtable]$Config) {

    $paths = [System.Collections.Generic.List[string]]::new()

    if ($null -eq $Config -or -not $Config.ContainsKey('TestEnvironment')) { return $paths }

    $te = $Config['TestEnvironment']

    if ($te -isnot [hashtable]) { return $paths }

    foreach ($k in @($te.Keys)) {

        if ($k -match 'StorageStatePath$' -and $null -ne $te[$k] -and "$($te[$k])" -ne '') {

            $paths.Add("$($te[$k])")

        }

    }

    if ($te.ContainsKey('Environments') -and $te['Environments'] -is [hashtable]) {

        foreach ($envKey in @($te['Environments'].Keys)) {

            $env = $te['Environments'][$envKey]

            if ($env -isnot [hashtable]) { continue }

            foreach ($k in @($env.Keys)) {

                if ($k -match 'StorageStatePath$' -and $null -ne $env[$k] -and "$($env[$k])" -ne '') {

                    $paths.Add("$($env[$k])")

                }

            }

        }

    }

    return @($paths | Select-Object -Unique)

}



function Copy-PackFiles([string]$From, [string]$To) {

    $selfPath = ''

    if (Test-Path "$From\install.ps1") { $selfPath = (Resolve-Path "$From\install.ps1").Path }

    Get-ChildItem -Path $From -Recurse | ForEach-Object {

        if ($_.FullName -eq $selfPath) { return }

        $rel  = $_.FullName.Substring($From.Length).TrimStart('\', '/')

        $dest = Join-Path $To $rel

        if ($_.PSIsContainer) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

        else {

            $destDir = Split-Path $dest -Parent

            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

            Copy-Item -Path $_.FullName -Destination $dest -Force

        }

    }

}


if ($MyInvocation.InvocationName -ne '.') {
$isUpgrade = Test-Path (Join-Path $InstallPath 'AiTestCrew.Runner.exe')

Write-Host ''

Write-Host 'AITestCrew Agent Installer' -ForegroundColor White

Write-Host ('-' * 50) -ForegroundColor DarkGray

Write-Info "Source:       $source"

Write-Info "Install path: $InstallPath"

Write-Info "Mode:         $(if ($isUpgrade) { 'Upgrade' } elseif ($CleanReinstall) { 'Clean reinstall' } else { 'First install' })"



if (-not $isUpgrade -and -not $CleanReinstall) {

    Write-Header 'First install'

    if (-not (Test-Path $InstallPath)) {

        try { New-Item -ItemType Directory -Path $InstallPath -Force -ErrorAction Stop | Out-Null }

        catch {

            $parent = Split-Path $InstallPath -Parent

            Write-Host ''

            Write-Host "ERROR: Could not create $InstallPath." -ForegroundColor Red

            Write-Host "Reason: $($_.Exception.Message)" -ForegroundColor Red

            Write-Host ''

            Write-Host "If '$parent' does not yet exist on this machine, Windows requires elevation to create it the first time." -ForegroundColor Yellow

            Write-Host 'Fixes (pick one):' -ForegroundColor Yellow

            Write-Host "  - Right-click install.cmd -> 'Run as administrator' (one-off; subsequent upgrades won't need it)." -ForegroundColor Yellow

            Write-Host "  - Or, from an elevated terminal, run:  New-Item -ItemType Directory $parent" -ForegroundColor Yellow

            Write-Host "    then re-run install.cmd as your normal user." -ForegroundColor Yellow

            Write-Host "  - Or, install to a per-user location:  install.cmd -InstallPath `"`$env:LOCALAPPDATA\AITestCrew\Agent`"" -ForegroundColor Yellow

            exit 1

        }

    }

    Copy-PackFiles $source $InstallPath

    Write-Ok "Installed $(Get-BuildLabel $InstallPath) to $InstallPath"

    Write-Host ''; Write-Host 'Next steps:' -ForegroundColor Cyan

    Write-Host '  1. Open appsettings.json and set your ApiKey.'

    Write-Host '  2. Run --auth-setup per environment/surface pair.'

    Write-Host "  3. Start the agent: $InstallPath\start-agent.cmd"

    Write-Host ''; return

}

if ($CleanReinstall) {

    Write-Header 'Clean reinstall (WARNING: nothing will be preserved)'

    $authDir   = Join-Path $InstallPath 'auth-state'

    $authCount = if (Test-Path $authDir) { @(Get-ChildItem -Recurse $authDir -File).Count } else { 0 }

    if ($authCount -gt 0) { Write-Warn "$authCount auth-state file(s) will be permanently lost." }

    Write-Warn 'All personal settings (ApiKey, WinFormsAppPath, etc.) will be lost.'

    Write-Host ''

    $confirm = Read-Host 'Proceed with clean reinstall? [y/N]'

    if ($confirm -notmatch '^[Yy]') { Write-Host 'Aborted.' -ForegroundColor Yellow; return }

}

Write-Header 'Upgrade'

$prevLabel = Get-BuildLabel $InstallPath; $newLabel = Get-BuildLabel $source

Write-Info "Previous build: $prevLabel"; Write-Info "New build:      $newLabel"

$preservedFiles = [System.Collections.Generic.List[hashtable]]::new()

if (-not $CleanReinstall) {

    foreach ($d in @('auth-state', 'logs')) {

        $fullDir = Join-Path $InstallPath $d

        if (Test-Path $fullDir) {

            Get-ChildItem -Recurse $fullDir -File | ForEach-Object {

                $rel = $_.FullName.Substring($InstallPath.Length).TrimStart('\', '/')

                $preservedFiles.Add(@{ Rel = $rel; Bytes = [System.IO.File]::ReadAllBytes($_.FullName) })

            }

        }

    }

    # Playwright storage-state files referenced by the existing appsettings.json --
    # the shipped defaults are bare filenames at install root (e.g. bravecloud-auth-state.json,
    # legacy-auth-state.sumo.json) which are NOT under auth-state/ and would otherwise be wiped.
    $existingCfgForStateScan = Join-Path $InstallPath 'appsettings.json'

    if (Test-Path $existingCfgForStateScan) {

        try {

            $cfgForScan = ConvertTo-Hashtable (Get-Content $existingCfgForStateScan -Raw | ConvertFrom-Json)

            foreach ($rel in (Get-StorageStatePaths $cfgForScan)) {

                if ([System.IO.Path]::IsPathRooted($rel)) { continue }

                $fullPath = Join-Path $InstallPath $rel

                if (-not (Test-Path $fullPath -PathType Leaf)) { continue }

                if ($preservedFiles | Where-Object { $_['Rel'] -eq $rel }) { continue }

                $preservedFiles.Add(@{ Rel = $rel; Bytes = [System.IO.File]::ReadAllBytes($fullPath) })

            }

        } catch {

            Write-Warn "Could not scan appsettings.json for storage-state paths: $_"

        }

    }

    $agentId = Join-Path $InstallPath 'agent-id.txt'

    if (Test-Path $agentId) {

        $preservedFiles.Add(@{ Rel = 'agent-id.txt'; Bytes = [System.IO.File]::ReadAllBytes($agentId) })

    }
    else {

        # Migration: pre-fix builds wrote the id to '.agent-id' (no extension).
        # Capture it under the canonical name so the stable identity survives this upgrade.
        $legacyAgentId = Join-Path $InstallPath '.agent-id'

        if (Test-Path $legacyAgentId) {

            $preservedFiles.Add(@{ Rel = 'agent-id.txt'; Bytes = [System.IO.File]::ReadAllBytes($legacyAgentId) })

        }

    }

}

$mergeResult = $null; $preservedSettings = @(); $removedSettings = @(); $backupFile = $null

if (-not $CleanReinstall) {

    $existingCfgPath = Join-Path $InstallPath 'appsettings.json'

    $incomingCfgPath = Join-Path $source 'appsettings.json'

    if ((Test-Path $existingCfgPath) -and (Test-Path $incomingCfgPath)) {

        $incomingCfg = ConvertTo-Hashtable (Get-Content $incomingCfgPath -Raw | ConvertFrom-Json)

        $existingCfg = ConvertTo-Hashtable (Get-Content $existingCfgPath -Raw | ConvertFrom-Json)

        $mergeResult = Merge-AppSettings -Incoming $incomingCfg -Existing $existingCfg

        $preservedSettings = $mergeResult['Preserved']; $removedSettings = $mergeResult['Removed']

        $mergedJson = $mergeResult['Merged'] | ConvertTo-Json -Depth 100

        try { $mergedJson | ConvertFrom-Json | Out-Null } catch {

            Write-Host 'ERROR: appsettings merge failed round-trip. Aborting - no files changed.' -ForegroundColor Red

            Write-Host "$_" -ForegroundColor Red; exit 1

        }

        $backupDir = Join-Path $InstallPath '.backup'

        if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }

        $ts = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'

        $backupFile = Join-Path $backupDir "appsettings.json.$ts"

        Copy-Item $existingCfgPath $backupFile -Force

        Write-Info "Backed up appsettings.json -> .backup/appsettings.json.$ts"

    }

}

$stoppedPids = @()

$runnerExe = Join-Path $InstallPath 'AiTestCrew.Runner.exe'

Get-Process -Name 'AiTestCrew.Runner' -ErrorAction SilentlyContinue | Where-Object {

    try { $_.Path -eq $runnerExe } catch { $false }

} | ForEach-Object { Write-Warn "Stopping AiTestCrew.Runner.exe (PID $($_.Id))..."; Stop-Process $_ -Force; $stoppedPids += $_.Id }

Get-ChildItem -Path $InstallPath | Where-Object { $_.Name -ne '.backup' } |

    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

Copy-PackFiles $source $InstallPath

foreach ($pf in $preservedFiles) {

    $dest = Join-Path $InstallPath $pf['Rel']; $destDir = Split-Path $dest -Parent

    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

    [System.IO.File]::WriteAllBytes($dest, $pf['Bytes'])

}

if ($null -ne $mergeResult) {

    $tgt = Join-Path $InstallPath 'appsettings.json'; $stg = "$tgt.new"

    $mergeResult['Merged'] | ConvertTo-Json -Depth 100 | Set-Content -Path $stg -Encoding UTF8

    Move-Item $stg $tgt -Force

}

$authKept  = @($preservedFiles | Where-Object { $_['Rel'] -like 'auth-state*' -or $_['Rel'] -like '*auth-state*.json' })

$otherKept = @($preservedFiles | Where-Object { $_['Rel'] -notlike 'auth-state*' -and $_['Rel'] -notlike '*auth-state*.json' })

Write-Host ''

Write-Host 'AITestCrew Agent - upgrade complete' -ForegroundColor Green

Write-Host "  Install path:     $InstallPath"

Write-Host "  Previous build:   $prevLabel"; Write-Host "  New build:        $newLabel"

Write-Host ''

if ($preservedSettings.Count -gt 0) {

    Write-Host "  Preserved $($preservedSettings.Count) setting(s):"

    foreach ($s in $preservedSettings) { Write-Host "    - $s" }

} else { Write-Host '  No settings carried over.' }

Write-Host ''

if ($authKept.Count -gt 0) {

    Write-Host "  Preserved $($authKept.Count) auth-state file(s):"

    foreach ($f in $authKept) { Write-Host "    $($f['Rel'])" }

} else { Write-Host '  No auth-state files found to preserve.' }

if ($otherKept.Count -gt 0) { Write-Host '  Also preserved:'; foreach ($f in $otherKept) { Write-Host "    $($f['Rel'])" } }

if ($null -ne $backupFile) { Write-Host "  Backup written to: $backupFile" }

if ($removedSettings.Count -gt 0) {

    Write-Host ''

    Write-Host "  Removed $($removedSettings.Count) setting(s) no longer in the new pack:" -ForegroundColor Yellow

    foreach ($s in $removedSettings) { Write-Host "    - $s" -ForegroundColor Yellow }

}

if ($stoppedPids.Count -gt 0) { Write-Host ''; Write-Host "  Stopped Runner PID(s): $($stoppedPids -join ', ')" -ForegroundColor Yellow }

$lc = "Upgraded $prevLabel -> $newLabel on $((Get-Date).ToString('o'))." + [System.Environment]::NewLine +

       "Preserved $($preservedSettings.Count) setting(s), $($authKept.Count) auth-state file(s)." + [System.Environment]::NewLine +

       "Install path: $InstallPath"

Set-Content -Path (Join-Path $InstallPath '.last-install.log') -Value $lc -Encoding UTF8

Write-Host ''; Write-Host 'Next:'

Write-Host "  - Start the agent: $InstallPath\start-agent.cmd"

Write-Host '  - Or wait for auto-start at next login (shell:startup shortcut).'

Write-Host ''
}  # end if not dot-sourced
