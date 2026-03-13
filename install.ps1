# EmailDownloader installer
# Usage: irm https://raw.githubusercontent.com/YOUR_USER/YOUR_REPO/master/install.ps1 | iex

$repo    = "imsaha/EmailDownloader"   # <-- update this
$tool    = "emaildl"
$dest    = "$env:USERPROFILE\.tools\$tool"
$exePath = "$dest\$tool.exe"
$cfgPath = "$dest\appsettings.json"

# Resolve latest release download URL
$release  = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$exeAsset = $release.assets | Where-Object { $_.name -eq "$tool.exe" }
$cfgAsset = $release.assets | Where-Object { $_.name -eq "appsettings.sample.json" }

if (-not $exeAsset) {
    Write-Error "Could not find $tool.exe in the latest release of $repo"
    exit 1
}

# Create install directory
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Download exe
Write-Host "Downloading $tool $($release.tag_name)..." -ForegroundColor Cyan
Invoke-WebRequest $exeAsset.browser_download_url -OutFile $exePath

# Download sample config if no config exists yet
if (-not (Test-Path $cfgPath) -and $cfgAsset) {
    Invoke-WebRequest $cfgAsset.browser_download_url -OutFile $cfgPath
    Write-Host "Config template saved to: $cfgPath" -ForegroundColor Yellow
    Write-Host "Edit it and add your Azure ClientId before first use." -ForegroundColor Yellow
}

# Add to PATH (current user, no admin required)
$currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($currentPath -notlike "*$dest*") {
    [Environment]::SetEnvironmentVariable("PATH", "$currentPath;$dest", "User")
    $env:PATH += ";$dest"
    Write-Host "Added $dest to PATH" -ForegroundColor Green
}

Write-Host ""
Write-Host "Installed: $exePath" -ForegroundColor Green
Write-Host "Run:       emaildl" -ForegroundColor Green
