# clibridge4unity installer for Windows
# Usage: irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex
#   or:  .\install.ps1                      # install latest
#   or:  .\install.ps1 -Version 1.0.90       # install specific version

param(
    [string]$Version = "",
    [string]$InstallDir = "$env:USERPROFILE\.clibridge4unity"
)

$ErrorActionPreference = "Stop"
$repo = "oddgames/clibridge4unity"

Write-Host "clibridge4unity installer" -ForegroundColor Cyan
Write-Host ""

# Determine version
if (-not $Version) {
    Write-Host "Fetching latest release..."
    try {
        $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
        $Version = $release.tag_name -replace '^v', ''
    } catch {
        Write-Host "Error: Could not fetch latest release from GitHub." -ForegroundColor Red
        Write-Host "       Check your internet connection or specify -Version manually."
        exit 1
    }
}

$tag = "v$Version"
$asset = "clibridge4unity-win-x64.zip"
$downloadUrl = "https://github.com/$repo/releases/download/$tag/$asset"

Write-Host "Version:  $Version"
Write-Host "Install:  $InstallDir"
Write-Host ""

# Create install directory
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Download
$zipPath = Join-Path $env:TEMP "clibridge4unity-$Version.zip"
Write-Host "Downloading $asset..."
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
} catch {
    Write-Host "Error: Download failed." -ForegroundColor Red
    Write-Host "       URL: $downloadUrl"
    Write-Host "       Ensure release '$tag' exists with asset '$asset'."
    exit 1
}

# Extract
Write-Host "Extracting..."
Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Remove-Item $zipPath -Force

# Verify
$exePath = Join-Path $InstallDir "clibridge4unity.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "Error: clibridge4unity.exe not found after extraction." -ForegroundColor Red
    exit 1
}

# Add to PATH if not already there
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$InstallDir*") {
    Write-Host "Adding to PATH..."
    [Environment]::SetEnvironmentVariable("Path", "$InstallDir;$userPath", "User")
    $env:Path = "$InstallDir;$env:Path"
    Write-Host "  Added $InstallDir to user PATH" -ForegroundColor Green
} else {
    Write-Host "  Already in PATH" -ForegroundColor Green
}

# Verify installation
Write-Host ""
& $exePath --version
Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Open a new terminal (for PATH changes)"
Write-Host "  2. cd to your Unity project"
Write-Host "  3. Run: clibridge4unity SETUP"
Write-Host ""
Write-Host "This will install the Unity package and set up CLAUDE.md for your project."
