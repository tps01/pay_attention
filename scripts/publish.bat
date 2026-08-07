@echo off
setlocal
cd /d "%~dp0.."

echo Publishing PayAttention (win-x64, self-contained, single-file)...

tasklist /FI "IMAGENAME eq PayAttention.exe" 2>NUL | find /I "PayAttention.exe" >NUL
if not errorlevel 1 (
  echo Stopping running PayAttention.exe so publish can overwrite it...
  taskkill /IM PayAttention.exe /F >NUL 2>&1
  ping 127.0.0.1 -n 2 >NUL
)

dotnet publish src/PayAttention -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -o publish
if errorlevel 1 (
  echo Publish failed.
  echo If the exe is still locked, close PayAttention from the tray and try again.
  exit /b 1
)

if not exist "publish\PayAttention.exe" (
  echo Expected output not found: publish\PayAttention.exe
  exit /b 1
)

echo.
echo Building user guide PDF...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-user-guide.ps1"
if errorlevel 1 (
  echo Warning: user guide PDF build failed. Exe publish succeeded anyway.
)

echo.
echo Done. Run: %cd%\publish\PayAttention.exe
if exist "publish\PayAttention-UserGuide.pdf" (
  echo User guide: %cd%\publish\PayAttention-UserGuide.pdf
) else (
  echo User guide: not built
)
echo You can double-click PayAttention.exe or create a shortcut.
endlocal
