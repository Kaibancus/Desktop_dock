#requires -Version 5.1
# Scheme 2: framework-dependent publish (target PC needs .NET 9 Desktop Runtime; tiny size)
#
# Usage:
#   .\publish-fd.ps1                 # build + package zip only
#   .\publish-fd.ps1 -Release v1.3.0 # also create/update the GitHub release and upload the zip
#
# Every run also drops a copy of the zip into the canonical local archive dir
# (-ArchiveDir, default C:\Tools\Polaris\publish-fd) unless that is already the
# build dir. Pass -ArchiveDir '' to disable.
param(
    [string]$Release,
    # Extra local archive dir to also drop the packaged zip into, so every
    # publish (even from a git worktree whose output lands elsewhere) keeps a
    # copy in the canonical local archive. Skipped automatically when it is the
    # same folder the zip is already built in. Override or pass '' to disable.
    [string]$ArchiveDir = 'C:\Tools\Polaris\publish-fd'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Derive the assembly version from the release tag (e.g. v1.7.8 -> 1.7.8) so the
# in-app updater's CurrentVersion always matches the published release. Without
# this the build falls back to csproj <Version>, which is easy to forget to bump.
$verArg = @()
if ($Release) {
    $ver = $Release -replace '^[vV]', ''
    if ($ver -match '^\d+(\.\d+){1,3}$') {
        $verArg = @("-p:Version=$ver")
        Write-Host "Stamping assembly version $ver from tag $Release" -ForegroundColor Cyan
    } else {
        Write-Warning "Release tag '$Release' is not a plain version; assembly version not stamped."
    }
}

# Stop any running instance so the output file is not locked
Get-Process Polaris -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

dotnet publish -c Release -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  @verArg `
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

# Also keep a copy in the canonical local archive dir. When the script runs from
# that dir directly (e.g. C:\Tools\Polaris), the zip is already there, so skip the
# self-copy. Never let an archive hiccup abort the actual release.
if ($ArchiveDir) {
    try {
        $zipDirFull = [System.IO.Path]::GetFullPath((Split-Path -Parent $zip))
        $archiveFull = [System.IO.Path]::GetFullPath($ArchiveDir)
        if ($archiveFull.TrimEnd('\') -ieq $zipDirFull.TrimEnd('\')) {
            Write-Host "Archive dir is the build dir; skipping the extra copy." -ForegroundColor DarkGray
        } else {
            if (-not (Test-Path $archiveFull)) { New-Item -ItemType Directory -Path $archiveFull -Force | Out-Null }
            Copy-Item $zip -Destination $archiveFull -Force
            Write-Host "Archived a copy to: $(Join-Path $archiveFull (Split-Path -Leaf $zip))" -ForegroundColor Green
        }
    } catch {
        Write-Warning "Could not archive the zip to '$ArchiveDir': $($_.Exception.Message)"
    }
}

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
