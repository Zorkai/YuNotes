@echo off
setlocal
cd /d "%~dp0"

rem Installing needs admin (to trust the dev certificate). If we're not elevated,
rem re-launch this same .bat elevated and let that instance do the work.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo   Installing YuNotes
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
echo Done. You can close this window.
pause
