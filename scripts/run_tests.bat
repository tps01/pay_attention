@echo off
setlocal
cd /d "%~dp0.."

set FAILED=0
set "PY=%CD%\.venv\Scripts\python.exe"
set "COLOSSEUM=%CD%\.venv\Scripts\colosseum.exe"

if not exist "%PY%" (
  echo Creating .venv...
  python -m venv "%CD%\.venv"
  if errorlevel 1 (
    echo Failed to create .venv.
    set FAILED=1
    goto :summary
  )
)

echo Installing Colosseum (core 0.16.6, gui 0.3.1)...
"%PY%" -m pip install -q ^
  https://github.com/tps01/colosseum-core/releases/download/v0.16.6/colosseum_core-0.16.6-py3-none-any.whl ^
  https://github.com/tps01/colosseum-gui/releases/download/v0.3.1/colosseum_gui-0.3.1-py3-none-any.whl
if errorlevel 1 (
  echo pip install failed.
  set FAILED=1
  goto :summary
)

echo.
echo Running Colosseum suite...
echo Includes the Release build and xunit tests. Keep harness windows uncovered.
"%COLOSSEUM%" run-suite tests/colosseum/suite.toml -g tests/colosseum/config.toml
if errorlevel 1 (
  echo Colosseum suite failed.
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
