@echo off
REM Thin wrapper around install.ps1 that handles the PowerShell execution-policy
REM headache in one place. Double-click this to install or upgrade the agent.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "INSTALL_EXIT=%ERRORLEVEL%"

echo.
if "%INSTALL_EXIT%"=="0" (
    echo Install finished. Press any key to close this window...
) else (
    echo Install exited with code %INSTALL_EXIT%. Press any key to close this window...
)
pause >nul
exit /b %INSTALL_EXIT%
