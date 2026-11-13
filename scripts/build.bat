@echo off
setlocal EnableExtensions
title Build Lark Cloud Drive
cd /d "%~dp0\.."
echo.
echo  ===== Building Lark Cloud Drive (files on demand) =====
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto nodotnet
dotnet --list-sdks | findstr /b "8. 9. 10." >nul
if errorlevel 1 goto nodotnet

echo [1/4] Closing Lark Cloud Drive if it is running...
taskkill /im LarkCloud.exe /f >nul 2>nul
timeout /t 2 /nobreak >nul

echo [2/4] Running the tests...
dotnet test tests\LarkCloud.Tests -c Release --nologo -v q
if errorlevel 1 (
  echo.
  echo  Some tests failed - nothing was built. Send the output above to IT.
  goto end
)

echo [3/4] Building LarkCloud.exe ...
if exist "dist\LarkCloud\LarkCloud.exe" del /f /q "dist\LarkCloud\LarkCloud.exe" >nul 2>nul
if exist "dist\LarkCloud\LarkCloud.exe" (
  echo.
  echo  dist\LarkCloud\LarkCloud.exe is in use. Quit Lark Cloud Drive from its tray icon
  echo  and close any window showing that folder, then run this again.
  goto end
)
dotnet publish src\LarkCloud.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --nologo -v q -o dist\LarkCloud
if errorlevel 1 (
  echo.
  echo  The build failed. Send the output above to IT.
  goto end
)
del /q "dist\LarkCloud\*.pdb" >nul 2>nul

echo [4/4] Settings (config.json)...
if exist "dist\LarkCloud\config.json" (
  echo   Keeping the existing dist\LarkCloud\config.json
) else if exist "..\lark-drive-sync\dist\LarkDriveSync\config.json" (
  copy /y "..\lark-drive-sync\dist\LarkDriveSync\config.json" "dist\LarkCloud\config.json" >nul
  echo   Copied the Lark app settings from the Lark Drive Sync build
) else if exist "..\lark-drive-sync\config.json" (
  copy /y "..\lark-drive-sync\config.json" "dist\LarkCloud\config.json" >nul
  echo   Copied the Lark app settings from lark-drive-sync\config.json
) else (
  copy /y "config.example.json" "dist\LarkCloud\config.json" >nul
  echo   Created dist\LarkCloud\config.json - fill in app_id and app_secret before running.
)

echo.
echo  Done:  dist\LarkCloud\LarkCloud.exe
echo  Hand out the whole dist\LarkCloud folder (LarkCloud.exe + config.json).
goto end

:nodotnet
echo  The .NET 8 SDK is needed to build (only on this PC - staff PCs don't need it).
echo  Opening the download page: install ".NET 8.0 SDK" (Windows x64), then run this again.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"

:end
echo.
pause
