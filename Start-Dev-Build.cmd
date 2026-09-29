@echo off
rem Builds AutoFantic from the source in this folder (with every change that isn't on GitHub yet)
rem and starts it, to try a change on this PC before pushing it. The build goes to dev\ (not in git)
rem and shows "-dev" after its version (e.g. 0.2.0-dev). It uses the same data as the installed AutoFantic.
setlocal
cd /d "%~dp0"

rem the .NET 10 SDK: installed for this user only, or system-wide
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

rem only one AutoFantic may drive the fans, and a running dev build can't be replaced
tasklist /FI "IMAGENAME eq AutoFantic.exe" 2>nul | find /I "AutoFantic.exe" >nul
if not errorlevel 1 (
    echo AutoFantic is running. Exit it first: right-click its icon next to the clock, then Exit.
    echo Then start this again.
    pause
    exit /b 1
)

echo Building AutoFantic from the source, about a minute ...
"%DOTNET%" publish src\AutoFantic.App -c Release -o dev -p:VersionSuffix=dev --nologo -v quiet
if errorlevel 1 goto failed
"%DOTNET%" publish src\AutoFantic.Spike -c Release -o dev -p:VersionSuffix=dev --nologo -v quiet
if errorlevel 1 goto failed

echo Starting the dev build. Windows asks for admin rights.
start "" "%~dp0dev\AutoFantic.exe" --open
exit /b 0

:failed
echo.
echo The build failed, see above. Nothing was started.
echo (It needs the .NET 10 SDK: https://dotnet.microsoft.com/download)
pause
exit /b 1
