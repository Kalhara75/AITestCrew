#!/usr/bin/env pwsh
# Bootstrap Pester 5 (Windows ships v3.4 which lacks Describe/It/Should -BeTrue)
# and run all .Tests.ps1 files in this directory.
param([switch]$CI)

$ErrorActionPreference = 'Stop'

$pester = Get-Module -ListAvailable -Name Pester | Sort-Object Version -Descending | Select-Object -First 1
if ($null -eq $pester -or $pester.Version.Major -lt 5) {
    Write-Host 'Pester 5 not found - installing to CurrentUser scope...' -ForegroundColor Yellow
    Install-Module -Name Pester -MinimumVersion 5.0 -Scope CurrentUser -Force -SkipPublisherCheck
}

Import-Module Pester -MinimumVersion 5.0 -Force

$config = New-PesterConfiguration
$config.Run.Path  = $PSScriptRoot
$config.Output.Verbosity = if ($CI) { 'Minimal' } else { 'Detailed' }
$config.TestResult.Enabled      = $CI.IsPresent
$config.TestResult.OutputPath   = (Join-Path $PSScriptRoot 'TestResults.xml')
$config.TestResult.OutputFormat = 'NUnitXml'

$result = Invoke-Pester -Configuration $config
if ($result.FailedCount -gt 0) { exit 1 }
