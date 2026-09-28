@echo off
rem Double-click to open AutoFantic. Windows asks once for admin rights (the fan chip needs them).

rem Newer builds waiting in publish-next\ replace the old ones (only while AutoFantic is closed).
if exist "%~dp0publish-next\*.exe" (
    for %%f in ("%~dp0publish-next\*.exe") do move /y "%%f" "%~dp0publish\" >nul 2>&1
)

rem Files from before the rename ("AutoFanatic"), removed once they're no longer running.
del /q "%~dp0publish\AutoFanatic.exe" "%~dp0publish\autofanatic-spike.exe" "%~dp0publish\AutoFanatic*.pdb" "%~dp0publish\autofanatic*.pdb" >nul 2>&1

if not exist "%~dp0publish\AutoFantic.exe" (
    echo publish\AutoFantic.exe is missing: ask Claude to build it.
    pause
    exit /b 1
)
start "" "%~dp0publish\AutoFantic.exe" --open
