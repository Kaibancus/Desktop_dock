#requires -Version 5.1
# Scheme 2: framework-dependent publish (target PC needs .NET 9 Desktop Runtime; tiny size)
#
# Usage:
#   .\publish-fd.ps1                 # build + package zip only
#   .\publish-fd.ps1 -Release v1.3.0 # also create/update the GitHub release and upload the zip
param(
    [string]$Release
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Stop any running instance so the output file is not locked
Get-Process Polaris -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

dotnet publish -c Release -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -o publish-fd

$exe = Join-Path $PSScriptRoot 'publish-fd\Polaris.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Publish failed: $exe not found"
    return
}

$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host "Published: $exe ($sizeMB MB)" -ForegroundColor Green

$hasRuntime = (dotnet --list-runtimes) -match 'Microsoft\.WindowsDesktop\.App 9\.'
if ($hasRuntime) {
    Write-Host "This PC has .NET 9 Desktop Runtime; ready to run." -ForegroundColor Green
} else {
    Write-Host "WARNING: .NET 9 Desktop Runtime not found. Target PC must install it:" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.DotNet.DesktopRuntime.9" -ForegroundColor Yellow
}

# Package the exe into a .zip — the in-app updater only performs online updates
# from a release whose asset is a .zip containing Polaris.exe (see UpdateService).
$tag = if ($Release) { $Release } else { 'dev' }
$zip = Join-Path $PSScriptRoot ("publish-fd\Polaris-{0}-fd-win-x64.zip" -f $tag)
Compress-Archive -Path $exe -DestinationPath $zip -Force
$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "Packaged: $zip ($zipMB MB)" -ForegroundColor Green

if (-not $Release) {
    Write-Host "Tip: pass -Release <tag> (e.g. -Release v1.3.1) to publish a GitHub release with this zip." -ForegroundColor DarkGray
    return
}

# Upload to GitHub: create the release if the tag has no release yet, then
# (re)upload the zip, replacing any existing same-named asset.
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) not found; cannot create the release. Install it or upload the zip manually: $zip"
    return
}

$exists = $true
try { gh release view $Release *> $null } catch { $exists = $false }
if ($LASTEXITCODE -ne 0) { $exists = $false }
if (-not $exists) {
    gh release create $Release $zip --title ("Polaris {0}" -f $Release) --notes ("Polaris {0}" -f $Release)
    Write-Host "Created release $Release and uploaded the zip." -ForegroundColor Green
} else {
    gh release upload $Release $zip --clobber
    Write-Host "Uploaded the zip to existing release $Release (replacing any same-named asset)." -ForegroundColor Green
}
