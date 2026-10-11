@echo off
setlocal
cd /d "%~dp0.."

set FAILED=0

echo Running .NET tests...
dotnet test PayAttention.sln -c Release --nologo
if errorlevel 1 (
  echo .NET tests failed.
  set FAILED=1
)

echo.
echo Installing Python test requirements...
python -m pip install -q -r tests\requirements.txt
if errorlevel 1 (
  echo pip install failed.
  set FAILED=1
  goto :summary
)

echo.
echo Running pytest...
python -m pytest tests -q
if errorlevel 1 (
  echo pytest failed.
  set FAILED=1
)

:summary
echo.
if "%FAILED%"=="1" (
  echo Tests finished with failures.
  exit /b 1
)

echo All tests passed.
exit /b 0
