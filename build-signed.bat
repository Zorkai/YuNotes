@echo off
REM Convenience wrapper around build-signed.ps1.
REM
REM Usage:
REM   build-signed.bat                  -- Debug build, self-sign
REM   build-signed.bat Release          -- Release build, self-sign
REM   build-signed.bat Debug run        -- Debug build, then launch
REM   build-signed.bat Release run      -- Release build, then launch
REM   build-signed.bat trust            -- one-time: install cert into LocalMachine\Root
REM                                       (right-click -> Run as administrator)

setlocal
set CONFIG=Debug
set EXTRA=

if /I "%~1"=="Debug"   set CONFIG=Debug   & shift
if /I "%~1"=="Release" set CONFIG=Release & shift

if /I "%~1"=="run"   set EXTRA=-Run
if /I "%~1"=="trust" set EXTRA=-TrustCert
if /I "%~2"=="run"   set EXTRA=-Run
if /I "%~2"=="trust" set EXTRA=-TrustCert

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-signed.ps1" -Configuration %CONFIG% %EXTRA%
set RC=%ERRORLEVEL%
echo.
if %RC% NEQ 0 (
    echo Build failed with exit code %RC%.
) else (
    echo Build finished successfully.
)
pause
exit /b %RC%
