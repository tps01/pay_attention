# Publishes a self-contained single-file PayAttention.exe to .\publish\
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Publishing PayAttention (win-x64, self-contained, single-file)..."

$running = Get-Process -Name "PayAttention" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running PayAttention.exe so publish can overwrite it..."
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
}

dotnet publish src/PayAttention `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishReadyToRun=true `
  -o publish

if ($LASTEXITCODE -ne 0) {
    Write-Host "If the exe is still locked, close PayAttention from the tray and try again."
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$exe = Join-Path $root "publish\PayAttention.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Expected output not found: $exe"
    exit 1
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Building user guide PDF..."
& (Join-Path $PSScriptRoot "build-user-guide.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Error "User guide PDF build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$guide = Join-Path $root "publish\PayAttention-UserGuide.pdf"
Write-Host ""
Write-Host "Done. Run: $exe ($sizeMb MB)"
Write-Host "User guide: $guide"
Write-Host "You can double-click PayAttention.exe or create a shortcut."
