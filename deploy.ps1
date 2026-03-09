# deploy.ps1 — Build, package, and release clibridge4unity
# Usage:
#   .\deploy.ps1                    # Build + upload assets to existing release (tag from csproj version)
#   .\deploy.ps1 -CreateRelease     # Build + create new GitHub release + upload assets
#   .\deploy.ps1 -SkipBuild        # Upload previously built assets only
#
# Prerequisites: dotnet, gh (GitHub CLI, authenticated), git

param(
    [switch]$CreateRelease,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$cliProject = Join-Path $root "clibridge4unity"
$csproj = Join-Path $cliProject "clibridge4unity.csproj"
$publishDir = Join-Path $cliProject "bin\Release\net8.0\win-x64\publish"
$packageTools = Join-Path $root "Package\Tools\win-x64"
$repo = "oddgames/clibridge4unity"

# Extract version from csproj
$csprojXml = [xml](Get-Content $csproj)
$version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    Write-Host "Error: Could not read <Version> from csproj" -ForegroundColor Red
    exit 1
}
$tag = "v$version"

Write-Host "=== clibridge4unity deploy ===" -ForegroundColor Cyan
Write-Host "Version: $version ($tag)"
Write-Host ""

# 1. Build
if (-not $SkipBuild) {
    Write-Host "[1/5] Building CLI..." -ForegroundColor Yellow
    Push-Location $cliProject
    try {
        dotnet publish -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    } finally {
        Pop-Location
    }

    $exe = Join-Path $publishDir "clibridge4unity.exe"
    if (-not (Test-Path $exe)) {
        Write-Host "Error: Published exe not found at $exe" -ForegroundColor Red
        exit 1
    }
    $exeSize = (Get-Item $exe).Length / 1MB
    Write-Host "  Built: clibridge4unity.exe ({0:N1} MB)" -f $exeSize -ForegroundColor Green
} else {
    Write-Host "[1/5] Skipping build" -ForegroundColor DarkGray
}

# 2. Create zip
Write-Host "[2/5] Packaging..." -ForegroundColor Yellow
$exe = Join-Path $publishDir "clibridge4unity.exe"
$zipPath = Join-Path $env:TEMP "clibridge4unity-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $exe -DestinationPath $zipPath -Force
$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host "  Created: clibridge4unity-win-x64.zip ({0:N1} MB)" -f $zipSize -ForegroundColor Green

# 3. Copy to Package/Tools
Write-Host "[3/5] Updating Package/Tools..." -ForegroundColor Yellow
if (-not (Test-Path $packageTools)) {
    New-Item -ItemType Directory -Path $packageTools -Force | Out-Null
}
Copy-Item $exe $packageTools -Force
Write-Host "  Copied exe to Package/Tools/win-x64/" -ForegroundColor Green

# 4. Create release if requested
if ($CreateRelease) {
    Write-Host "[4/5] Creating GitHub release $tag..." -ForegroundColor Yellow

    # Check if tag exists
    $existingTag = git tag -l $tag 2>$null
    if (-not $existingTag) {
        git tag $tag
        git push origin $tag
        Write-Host "  Created and pushed tag $tag" -ForegroundColor Green
    } else {
        Write-Host "  Tag $tag already exists" -ForegroundColor DarkGray
    }

    # Check if release exists
    $releaseExists = gh release view $tag 2>$null
    if ($LASTEXITCODE -ne 0) {
        gh release create $tag --title $tag --generate-notes
        Write-Host "  Created release $tag" -ForegroundColor Green
    } else {
        Write-Host "  Release $tag already exists" -ForegroundColor DarkGray
    }
} else {
    Write-Host "[4/5] Skipping release creation (use -CreateRelease)" -ForegroundColor DarkGray
}

# 5. Upload assets
Write-Host "[5/5] Uploading assets to $tag..." -ForegroundColor Yellow
gh release upload $tag $zipPath --clobber
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Asset upload failed" -ForegroundColor Red
    exit 1
}
Write-Host "  Uploaded clibridge4unity-win-x64.zip" -ForegroundColor Green

# Also upload bare exe for direct download
gh release upload $tag $exe --clobber
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Warning: bare exe upload failed (non-critical)" -ForegroundColor Yellow
} else {
    Write-Host "  Uploaded clibridge4unity.exe" -ForegroundColor Green
}

# Done
Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Cyan
Write-Host "Release: https://github.com/$repo/releases/tag/$tag"
Write-Host ""
Write-Host "Install command:" -ForegroundColor Yellow
Write-Host "  irm https://raw.githubusercontent.com/$repo/main/install.ps1 | iex"
