@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Building YuNotes Store package (.msixupload)
echo   Unsigned - Microsoft signs Store packages.
echo   (close the app first if it's running)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-store.ps1"
echo.
echo Done. You can close this window.
echo Next: run WACK, then upload the .msixupload in Partner Center.
pause
