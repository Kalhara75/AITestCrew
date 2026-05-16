@echo off
REM Thin wrapper around install.ps1 that handles the PowerShell execution-policy
REM headache in one place. Double-click this to install or upgrade the agent.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
