@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Uninstalling YuNotes
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
echo Done. You can close this window.
pause
