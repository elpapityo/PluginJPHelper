@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

title Plugin JP Helper Build

set "PLUGIN=PluginJPHelper"
set "VERSION=0.4.12"
set "CONFIG=Release"
set "PROJECT=%~dp0PluginJPHelper\PluginJPHelper.csproj"
set "SOURCEICON=%~dp0images\icon.png"
set "SOURCEDICT=%~dp0Dictionaries"
set "OUTDIR=%~dp0PluginJPHelper\bin\x64\Release"
set "BUILDDLL=%OUTDIR%\PluginJPHelper.dll"
set "LOCAL=Z:\PluginJPHelper\Current"
set "RELEASE=%~dp0release\PluginJPHelper"
set "ZIPFILE=%~dp0release\PluginJPHelper_v%VERSION%.zip"

echo ================================================
echo Plugin JP Helper v%VERSION% Build
echo ================================================
echo.

echo [1/5] Restoring...
dotnet restore "%PROJECT%"
if errorlevel 1 goto :fail

echo.
echo [2/5] Building...
dotnet build "%PROJECT%" -c %CONFIG% --no-restore -p:Platform=x64
if errorlevel 1 goto :fail

if not exist "%BUILDDLL%" (
    set "BUILDDLL="
    for /f "delims=" %%F in ('dir /b /s "%~dp0PluginJPHelper\bin\%CONFIG%\PluginJPHelper.dll" 2^>nul') do (
        if not defined BUILDDLL set "BUILDDLL=%%~fF"
    )
)

if not defined BUILDDLL (
    echo [ERROR] PluginJPHelper.dll was not found.
    goto :fail
)
if not exist "!BUILDDLL!" (
    echo [ERROR] Built DLL does not exist.
    goto :fail
)

for %%F in ("!BUILDDLL!") do set "BUILDDIR=%%~dpF"
echo [OK] DLL: !BUILDDLL!

if not exist "%SOURCEICON%" (
    echo [ERROR] images\icon.png was not found.
    goto :fail
)
echo [OK] Icon: %SOURCEICON%

echo.
echo [3/5] Preparing release folder...
if exist "%RELEASE%" rmdir /s /q "%RELEASE%"
mkdir "%RELEASE%" >nul 2>nul
if errorlevel 1 goto :fail

copy /Y "!BUILDDLL!" "%RELEASE%\PluginJPHelper.dll" >nul
if errorlevel 1 (
    echo [ERROR] Failed to copy PluginJPHelper.dll.
    goto :fail
)

for %%F in (PluginJPHelper.deps.json Microsoft.Windows.SDK.NET.dll WinRT.Runtime.dll) do (
    if not exist "!BUILDDIR!%%F" (
        echo [ERROR] Required build file is missing: %%F
        goto :fail
    )
    copy /Y "!BUILDDIR!%%F" "%RELEASE%\%%F" >nul
    if errorlevel 1 (
        echo [ERROR] Failed to copy: %%F
        goto :fail
    )
)

if not exist "%RELEASE%\images" mkdir "%RELEASE%\images" >nul 2>nul
copy /Y "%SOURCEICON%" "%RELEASE%\images\icon.png" >nul
if errorlevel 1 (
    echo [ERROR] Failed to copy icon.
    goto :fail
)

rem 同梱するのはプラグインが実行時に読み込む辞書だけにする。
rem Dictionaries\Official と Dictionaries\Community は GitHub 配布用の置き場で、
rem 実行時はユーザーの設定フォルダー側 (ConfigDirectory\Dictionaries\...) を参照するため
rem 同梱しても読まれない。再帰コピーすると配布 ZIP が 3MB 以上膨らむ。
if exist "%SOURCEDICT%" (
    if exist "%RELEASE%\Dictionaries" rmdir /s /q "%RELEASE%\Dictionaries"
    mkdir "%RELEASE%\Dictionaries" >nul 2>nul
    copy /Y "%SOURCEDICT%\*.csv" "%RELEASE%\Dictionaries\" >nul
    if errorlevel 1 (
        echo [ERROR] Failed to copy bundled dictionaries.
        goto :fail
    )
    if exist "%SOURCEDICT%\PluginInstaller" (
        mkdir "%RELEASE%\Dictionaries\PluginInstaller" >nul 2>nul
        xcopy "%SOURCEDICT%\PluginInstaller\*" "%RELEASE%\Dictionaries\PluginInstaller\" /E /I /Y /Q >nul
        if errorlevel 1 (
            echo [ERROR] Failed to copy PluginInstaller dictionaries.
            goto :fail
        )
    )
)

if not exist "%RELEASE%\PluginJPHelper.dll" (
    echo [ERROR] Release DLL is missing.
    goto :fail
)
if not exist "%RELEASE%\PluginJPHelper.json" (
    copy /Y "%~dp0PluginJPHelper\PluginJPHelper.json" "%RELEASE%\PluginJPHelper.json" >nul
)
if not exist "%RELEASE%\PluginJPHelper.json" (
    echo [ERROR] Release JSON is missing.
    goto :fail
)
if not exist "%RELEASE%\images\icon.png" (
    echo [ERROR] Release icon is missing.
    goto :fail
)

echo [OK] Release DLL: %RELEASE%\PluginJPHelper.dll
echo [OK] Release icon: %RELEASE%\images\icon.png

echo.
echo [4/5] Copying to local test folder...
if not exist "Z:\" (
    echo [ERROR] Z: drive was not found.
    echo Target: %LOCAL%
    goto :fail
)

if exist "%LOCAL%" rmdir /s /q "%LOCAL%"
mkdir "%LOCAL%" >nul 2>nul
xcopy "%RELEASE%\*" "%LOCAL%\" /E /I /Y /Q >nul
if errorlevel 1 (
    echo [ERROR] Failed to copy to Z: drive.
    goto :fail
)

if not exist "%LOCAL%\PluginJPHelper.dll" (
    echo [ERROR] Local DLL is missing.
    goto :fail
)
if not exist "%LOCAL%\images\icon.png" (
    echo [ERROR] Local icon is missing.
    goto :fail
)

echo [OK] Local DLL: %LOCAL%\PluginJPHelper.dll
echo [OK] Local icon: %LOCAL%\images\icon.png

echo.
echo [5/5] Creating GitHub upload ZIP...
if not exist "%~dp0release" mkdir "%~dp0release" >nul 2>nul
if exist "%ZIPFILE%" del /q "%ZIPFILE%"

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%RELEASE%\*' -DestinationPath '%ZIPFILE%' -CompressionLevel Optimal -Force"
if errorlevel 1 (
    echo [ERROR] Failed to create ZIP.
    goto :fail
)

if not exist "%ZIPFILE%" (
    echo [ERROR] ZIP file was not created.
    goto :fail
)

for %%F in ("%ZIPFILE%") do set "ZIPSIZE=%%~zF"
if "!ZIPSIZE!"=="0" (
    echo [ERROR] ZIP file is empty.
    goto :fail
)

echo [OK] Release ZIP: %ZIPFILE%
echo [OK] ZIP size: !ZIPSIZE! bytes

echo.
echo ================================================
echo Build completed successfully
echo ================================================
echo.
echo Local test:
echo   %LOCAL%\PluginJPHelper.dll
echo   %LOCAL%\PluginJPHelper.json
echo   %LOCAL%\images\icon.png
echo.
echo GitHub upload:
echo   %ZIPFILE%
echo.
echo This BAT does not upload or publish to GitHub.
echo.
pause
exit /b 0

:fail
echo.
echo ================================================
echo Build or deployment failed
echo ================================================
echo.
echo Please send the full contents of this window.
echo.
pause
exit /b 1
