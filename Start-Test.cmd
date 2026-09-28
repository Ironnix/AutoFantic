@echo off
rem Double-click to start the AutoFanatic hardware test. Windows asks once for admin rights.
if not exist "%~dp0publish\autofanatic-spike.exe" (
    echo publish\autofanatic-spike.exe is missing: ask Claude to build it.
    pause
    exit /b 1
)
start "" "%~dp0publish\autofanatic-spike.exe" test
