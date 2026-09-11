@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK 10 not found. Install from https://dotnet.microsoft.com/download
    exit /b 1
)
dotnet --list-sdks | findstr /b "10\." >nul
if errorlevel 1 (
    echo .NET 10 SDK not found. Install from https://dotnet.microsoft.com/download
    exit /b 1
)

set "RID=win-x64"
set "VER="
for %%A in (%*) do (
    if /i "%%~A"=="x64" (set "RID=win-x64") else (set "VER=%%~A")
)
if defined VER call :stripv
if defined VER call :checkver
if errorlevel 1 exit /b 1

taskkill /F /IM TurboToggle.exe >nul 2>nul
tasklist /FI "IMAGENAME eq TurboToggle.exe" 2>nul | find /i "TurboToggle.exe" >nul
if not errorlevel 1 (
    echo WARNING: could not kill running TurboToggle.exe - run publish.bat as admin or Quit from tray.
)

if defined VER (
    echo Building TurboToggle %VER% for %RID% - self-contained, single file...
    dotnet publish TurboToggle.csproj -c Release -r %RID% --self-contained ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
      -p:Version=%VER%
) else (
    echo Building TurboToggle for %RID% - self-contained, single file...
    dotnet publish TurboToggle.csproj -c Release -r %RID% --self-contained ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
)

if errorlevel 1 (
    echo.
    echo Build FAILED.
    if "%CI%"=="" pause
    exit /b 1
)

echo.
echo Done: %~dp0bin\Release\net10.0-windows\%RID%\publish\TurboToggle.exe
echo Usage: publish.bat [x64] [version]
if "%CI%"=="" if "%~1"=="" pause
exit /b 0

:stripv
if /i "%VER:~0,1%"=="v" set "VER=%VER:~1%"
exit /b 0

:checkver
set "VCHK=%VER%"
set "PARTS=0"
if "%VCHK%"=="" goto :badver
if "%VCHK:.=%"=="%VCHK%" goto :badver
if "%VCHK:~0,1%"=="." goto :badver
if "%VCHK:~-1%"=="." goto :badver
if not "%VCHK:..=%"=="%VCHK%" goto :badver
:verloop
set /a PARTS+=1
if %PARTS% gtr 4 goto :badver
for /f "tokens=1* delims=." %%a in ("%VCHK%") do (
    if "%%a"=="" goto :badver
    for /f "delims=0123456789" %%d in ("%%a") do goto :badver
    set "VCHK=%%b"
    if not "%%b"=="" goto :verloop
)
exit /b 0
:badver
echo Bad version "%VER%" - use like 1.1 or 1.2.3
exit /b 1
