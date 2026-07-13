@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Building YuNotes release ZIP
echo   (close the app first if it's running)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-release.ps1"
echo.
echo Done. The .zip is in packaging\release\ - drag it onto a GitHub release.
pause
