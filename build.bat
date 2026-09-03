@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "VERSION=0.3.1"
set "PROJECT=%~dp0PluginJPHelper\PluginJPHelper.csproj"
set "MANIFEST=%~dp0PluginJPHelper\PluginJPHelper.json"
set "OUTDIR=%~dp0PluginJPHelper\bin\x64\Release"
set "TESTDIR=Z:\PluginJPHelper\Current"
set "RELEASEDIR=%~dp0release\PluginJPHelper"
set "ZIPFILE=%~dp0release\PluginJPHelper_v%VERSION%.zip"

echo ================================================
echo Plugin JP Helper v%VERSION% LOCAL TEST + RELEASE PACKAGE BUILD
echo ================================================
echo.
echo Test deploy target:
echo %TESTDIR%
echo.
echo Release ZIP:
echo %ZIPFILE%
echo.

findstr /C:"\"AssemblyVersion\": \"0.3.1.0\"" "%MANIFEST%" >nul
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
if not exist "Z:\" goto :z_failed

rem ===== Local test deployment =====
if exist "%TESTDIR%" rmdir /s /q "%TESTDIR%"
mkdir "%TESTDIR%"
call :copy_plugin_files "%TESTDIR%"

rem ===== Release package =====
if exist "%RELEASEDIR%" rmdir /s /q "%RELEASEDIR%"
mkdir "%RELEASEDIR%"
call :copy_plugin_files "%RELEASEDIR%"

if exist "%ZIPFILE%" del /q "%ZIPFILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%RELEASEDIR%\*' -DestinationPath '%ZIPFILE%' -CompressionLevel Optimal -Force"
if errorlevel 1 goto :zip_failed

if not exist "%ZIPFILE%" goto :zip_failed

echo.
echo ================================================
echo BUILD OK
echo ================================================
echo.
echo Local test:
echo %TESTDIR%
echo.
echo Release upload ZIP:
echo %ZIPFILE%
echo.
echo ZIP contents are directly under the archive root.
echo.
pause
exit /b 0

:copy_plugin_files
set "DEST=%~1"
copy /y "%BUILDDIR%PluginJPHelper.dll" "%DEST%\PluginJPHelper.dll" >nul
if exist "%BUILDDIR%PluginJPHelper.deps.json" copy /y "%BUILDDIR%PluginJPHelper.deps.json" "%DEST%\PluginJPHelper.deps.json" >nul
if exist "%BUILDDIR%PluginJPHelper.runtimeconfig.json" copy /y "%BUILDDIR%PluginJPHelper.runtimeconfig.json" "%DEST%\PluginJPHelper.runtimeconfig.json" >nul
copy /y "%MANIFEST%" "%DEST%\PluginJPHelper.json" >nul
if exist "%BUILDDIR%Microsoft.Windows.SDK.NET.dll" copy /y "%BUILDDIR%Microsoft.Windows.SDK.NET.dll" "%DEST%\Microsoft.Windows.SDK.NET.dll" >nul
if exist "%BUILDDIR%WinRT.Runtime.dll" copy /y "%BUILDDIR%WinRT.Runtime.dll" "%DEST%\WinRT.Runtime.dll" >nul
if exist "%~dp0Dictionaries" (
    mkdir "%DEST%\Dictionaries" >nul 2>&1
    xcopy /e /i /y "%~dp0Dictionaries\*" "%DEST%\Dictionaries\" >nul
)
exit /b 0

:manifest_failed
echo.
echo MANIFEST ERROR: AssemblyVersion 0.3.1.0 not found.
pause
exit /b 1

:build_failed
echo.
echo BUILD FAILED
pause
exit /b 1

:zip_failed
echo.
echo ZIP PACKAGE FAILED
pause
exit /b 1

:z_failed
echo.
echo Z DRIVE NOT FOUND
pause
exit /b 1
