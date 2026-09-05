@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem -----------------------------------------------------------------
rem Outer launcher: even if the inner build aborts, this window stays open.
rem -----------------------------------------------------------------
if /i "%~1"=="__PJH_BUILD_INNER__" goto :inner

cd /d "%~dp0"
title Plugin JP Helper v0.4.0 Build
cmd /d /c call "%~f0" __PJH_BUILD_INNER__
set "FINAL_RC=%ERRORLEVEL%"
echo.
echo ================================================================
if "%FINAL_RC%"=="0" echo Build process finished successfully.
if not "%FINAL_RC%"=="0" echo Build process stopped with error code %FINAL_RC%.
echo ================================================================
echo.
echo Press any key to close this window.
pause >nul
exit /b %FINAL_RC%

:inner
shift
cd /d "%~dp0"
chcp 65001 >nul 2>&1

set "VERSION=0.4.0"
set "PROJECT=%~dp0PluginJPHelper\PluginJPHelper.csproj"
set "MANIFEST=%~dp0PluginJPHelper\PluginJPHelper.json"
set "OUTDIR=%~dp0PluginJPHelper\bin\x64\Release"
set "TESTDIR=Z:\PluginJPHelper\Current"
set "RELEASEDIR=%~dp0release\PluginJPHelper"
set "ZIPFILE=%~dp0release\PluginJPHelper_v%VERSION%.zip"
set "LOGFILE=%~dp0build_log.txt"

echo ================================================================
echo Plugin JP Helper v%VERSION% Build
echo ================================================================
echo.
echo Build log: %LOGFILE%
echo.

where dotnet >nul 2>&1
if errorlevel 1 goto :no_dotnet
if not exist "%PROJECT%" goto :no_project
if not exist "%MANIFEST%" goto :no_manifest

powershell -NoProfile -ExecutionPolicy Bypass -Command "$raw=[System.IO.File]::ReadAllText($env:MANIFEST,[System.Text.Encoding]::UTF8); $j=ConvertFrom-Json -InputObject $raw; if($j.AssemblyVersion -ne '0.4.0.0'){Write-Host ('[ERROR] AssemblyVersion=' + $j.AssemblyVersion); exit 1}"
if errorlevel 1 goto :failed

echo [1/4] Building...
dotnet build "%PROJECT%" -c Release -p:Platform=x64 > "%LOGFILE%" 2>&1
set "BUILD_RC=%ERRORLEVEL%"
type "%LOGFILE%"
if not "%BUILD_RC%"=="0" goto :failed

set "BUILDDLL="
for /f "delims=" %%F in ('dir /b /s "%OUTDIR%\PluginJPHelper.dll" 2^>nul') do call :set_first_dll "%%F"
if not defined BUILDDLL goto :no_dll
for %%F in ("%BUILDDLL%") do set "BUILDDIR=%%~dpF"

echo.
echo [2/4] Copying local test files...
if not exist Z:\ goto :skip_test
if exist "%TESTDIR%" rmdir /s /q "%TESTDIR%"
mkdir "%TESTDIR%" >nul 2>&1
if errorlevel 1 goto :failed
call :copy_plugin_files "%TESTDIR%"
if errorlevel 1 goto :failed
echo       %TESTDIR%
goto :after_test

:skip_test
echo       Z: drive not found. Local test copy skipped.

:after_test
echo.
echo [3/4] Creating release folder...
if exist "%RELEASEDIR%" rmdir /s /q "%RELEASEDIR%"
mkdir "%RELEASEDIR%" >nul 2>&1
if errorlevel 1 goto :failed
call :copy_plugin_files "%RELEASEDIR%"
if errorlevel 1 goto :failed

echo.
echo [4/4] Creating release ZIP...
if exist "%ZIPFILE%" del /q "%ZIPFILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path (Join-Path $env:RELEASEDIR '*') -DestinationPath $env:ZIPFILE -CompressionLevel Optimal -Force"
if errorlevel 1 goto :failed
if not exist "%ZIPFILE%" goto :no_zip

powershell -NoProfile -ExecutionPolicy Bypass -Command "$z=Get-Item -LiteralPath $env:ZIPFILE; if($z.Length -le 0){exit 1}; Write-Host ('ZIP size: ' + $z.Length + ' bytes')"
if errorlevel 1 goto :failed

echo.
echo ================================================================
echo BUILD SUCCESS
echo ================================================================
echo Upload this ZIP to GitHub:
echo %ZIPFILE%
exit /b 0

:set_first_dll
if defined BUILDDLL exit /b 0
set "BUILDDLL=%~1"
exit /b 0

:copy_plugin_files
set "DEST=%~1"
copy /y "%BUILDDIR%PluginJPHelper.dll" "%DEST%\PluginJPHelper.dll" >nul
if errorlevel 1 exit /b 1
if exist "%BUILDDIR%PluginJPHelper.deps.json" copy /y "%BUILDDIR%PluginJPHelper.deps.json" "%DEST%\PluginJPHelper.deps.json" >nul
if errorlevel 1 exit /b 1
if exist "%BUILDDIR%PluginJPHelper.runtimeconfig.json" copy /y "%BUILDDIR%PluginJPHelper.runtimeconfig.json" "%DEST%\PluginJPHelper.runtimeconfig.json" >nul
if errorlevel 1 exit /b 1
copy /y "%MANIFEST%" "%DEST%\PluginJPHelper.json" >nul
if errorlevel 1 exit /b 1
if exist "%BUILDDIR%Microsoft.Windows.SDK.NET.dll" copy /y "%BUILDDIR%Microsoft.Windows.SDK.NET.dll" "%DEST%\Microsoft.Windows.SDK.NET.dll" >nul
if errorlevel 1 exit /b 1
if exist "%BUILDDIR%WinRT.Runtime.dll" copy /y "%BUILDDIR%WinRT.Runtime.dll" "%DEST%\WinRT.Runtime.dll" >nul
if errorlevel 1 exit /b 1
if not exist "%~dp0Dictionaries" exit /b 0
mkdir "%DEST%\Dictionaries" >nul 2>&1
xcopy /e /i /y "%~dp0Dictionaries\*" "%DEST%\Dictionaries\" >nul
if errorlevel 1 exit /b 1
exit /b 0

:no_dotnet
echo [ERROR] dotnet was not found in PATH.
goto :failed

:no_project
echo [ERROR] Project file was not found:
echo %PROJECT%
goto :failed

:no_manifest
echo [ERROR] PluginJPHelper.json was not found:
echo %MANIFEST%
goto :failed

:no_dll
echo [ERROR] PluginJPHelper.dll was not found after build.
goto :failed

:no_zip
echo [ERROR] Release ZIP was not created.
goto :failed

:failed
echo.
echo ================================================================
echo BUILD FAILED
echo ================================================================
echo Check the error above and this log:
echo %LOGFILE%
exit /b 1
