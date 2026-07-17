@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Building standalone single-file YuNotes.exe
echo   (no install, no folder - one .exe)
echo   (close the app first if it's running)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-exe.ps1"
echo.
echo Done. You can close this window.
echo Output: packaging\exe-output\YuNotes.exe
pause
