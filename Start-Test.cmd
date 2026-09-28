@echo off
rem Double-click to start AutoFanatic's test menu. Windows asks once for admin rights.

rem Newer builds waiting in publish-next\ replace the old ones (only works while no AutoFanatic program is open).
if exist "%~dp0publish-next\*.exe" (
    for %%f in ("%~dp0publish-next\*.exe") do move /y "%%f" "%~dp0publish\" >nul 2>&1
    if exist "%~dp0publish-next\*.exe" (
        echo An AutoFanatic program is still open, so the new version can't be installed yet.
        echo Close it ^(or the icon next to the clock: right-click, Exit^), then start this again.
        echo Starting the current version for now.
        pause
    )
)

if not exist "%~dp0publish\autofanatic-spike.exe" (
    echo publish\autofanatic-spike.exe is missing: ask Claude to build it.
    pause
    exit /b 1
)
start "" "%~dp0publish\autofanatic-spike.exe" test
