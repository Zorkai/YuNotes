@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Building signed YuNotes .msix
echo   (close the app first if it's running)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
echo Done. You can close this window.
echo Next: double-click "Install YuNotes.bat"
pause
