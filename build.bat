@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "VERSION=0.1.0"
set "PROJECT=%~dp0PluginJPHelper\PluginJPHelper.csproj"
set "MANIFEST=%~dp0PluginJPHelper\PluginJPHelper.json"
set "OUTDIR=%~dp0PluginJPHelper\bin\x64\Release"
set "RELEASEROOT=%~dp0release"
set "PACKDIR=%RELEASEROOT%\PluginJPHelper"
set "ZIPFILE=%RELEASEROOT%\PluginJPHelper_v%VERSION%.zip"
set "TESTROOT=Z:\PluginJPHelper"
set "TESTDIR=%TESTROOT%\Current"

echo ================================================
echo Plugin JP Helper v%VERSION% build / release
echo ================================================
echo.

findstr /C:"\"AssemblyVersion\": \"0.1.0.0\"" "%MANIFEST%" >nul
if errorlevel 1 goto :manifest_failed

dotnet build "%PROJECT%" -c Release -p:Platform=x64
if errorlevel 1 goto :build_failed

set "BUILDDIR="
for /r "%OUTDIR%" %%F in (PluginJPHelper.dll) do (
    set "BUILDDIR=%%~dpF"
    goto :build_dir_found
)

:build_dir_found
if not defined BUILDDIR goto :build_failed

if exist "%PACKDIR%" rmdir /s /q "%PACKDIR%"
if not exist "%RELEASEROOT%" mkdir "%RELEASEROOT%"
mkdir "%PACKDIR%"

copy /y "%BUILDDIR%PluginJPHelper.dll" "%PACKDIR%\PluginJPHelper.dll" >nul
if exist "%BUILDDIR%PluginJPHelper.deps.json" copy /y "%BUILDDIR%PluginJPHelper.deps.json" "%PACKDIR%\PluginJPHelper.deps.json" >nul
if exist "%BUILDDIR%PluginJPHelper.runtimeconfig.json" copy /y "%BUILDDIR%PluginJPHelper.runtimeconfig.json" "%PACKDIR%\PluginJPHelper.runtimeconfig.json" >nul
copy /y "%MANIFEST%" "%PACKDIR%\PluginJPHelper.json" >nul

rem Copy only runtime dependencies referenced by the current build when present.
if exist "%BUILDDIR%Microsoft.Windows.SDK.NET.dll" copy /y "%BUILDDIR%Microsoft.Windows.SDK.NET.dll" "%PACKDIR%\Microsoft.Windows.SDK.NET.dll" >nul
if exist "%BUILDDIR%WinRT.Runtime.dll" copy /y "%BUILDDIR%WinRT.Runtime.dll" "%PACKDIR%\WinRT.Runtime.dll" >nul

if exist "%~dp0Dictionaries" (
    mkdir "%PACKDIR%\Dictionaries" >nul 2>&1
    xcopy /e /i /y "%~dp0Dictionaries\*" "%PACKDIR%\Dictionaries\" >nul
)

if exist "%ZIPFILE%" del /q "%ZIPFILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PACKDIR%\*' -DestinationPath '%ZIPFILE%' -Force"
if errorlevel 1 goto :zip_failed

rem Optional local test deployment. Failure here does not invalidate the release ZIP.
if exist "Z:\" (
    if not exist "%TESTROOT%" mkdir "%TESTROOT%"
    if exist "%TESTDIR%" rmdir /s /q "%TESTDIR%"
    mkdir "%TESTDIR%"
    xcopy /e /i /y "%PACKDIR%\*" "%TESTDIR%\" >nul
)

echo.
echo ================================================
echo BUILD / RELEASE OK
echo ================================================
echo Release folder:
echo %PACKDIR%
echo.
echo Release ZIP:
echo %ZIPFILE%
echo.
echo ZIP contents:
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead('%ZIPFILE%').Entries | ForEach-Object { $_.FullName }"
echo.
pause
exit /b 0

:manifest_failed
echo.
echo MANIFEST ERROR: AssemblyVersion 0.1.0.0 not found.
pause
exit /b 1

:build_failed
echo.
echo BUILD FAILED
pause
exit /b 1

:zip_failed
echo.
echo ZIP CREATE FAILED
pause
exit /b 1
