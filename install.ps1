# EmailDownloader installer
# Usage: irm https://raw.githubusercontent.com/imsaha/EmailDownloader/master/install.ps1 | iex

$repo    = "imsaha/EmailDownloader"
$tool    = "emaildl"
$dest    = "$env:USERPROFILE\.tools\$tool"
$exePath = "$dest\$tool.exe"
$cfgPath = "$dest\appsettings.json"

# Resolve latest release
$release  = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$exeAsset = $release.assets | Where-Object { $_.name -eq "$tool.exe" }
$cfgAsset = $release.assets | Where-Object { $_.name -eq "appsettings.sample.json" }

if (-not $exeAsset) {
    Write-Error "Could not find $tool.exe in the latest release of $repo"
    exit 1
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null

# --- Exe: update only if version changed ---
$latestTag    = $release.tag_name
$versionFile  = "$dest\version.txt"
$installedTag = if (Test-Path $versionFile) { Get-Content $versionFile -Raw | ForEach-Object { $_.Trim() } } else { "" }

if ($installedTag -eq $latestTag) {
    Write-Host "$tool $latestTag is already up to date." -ForegroundColor Green
} else {
    Write-Host "$(if ($installedTag) { "Updating $tool $installedTag -> $latestTag" } else { "Installing $tool $latestTag" })..." -ForegroundColor Cyan
    Invoke-WebRequest $exeAsset.browser_download_url -OutFile $exePath
    Set-Content $versionFile $latestTag
    Write-Host "Exe updated." -ForegroundColor Green
}

# --- Config: only write on first install, never overwrite ---
if (Test-Path $cfgPath) {
    Write-Host "Existing config kept: $cfgPath" -ForegroundColor Gray
} else {
    if ($cfgAsset) {
        Invoke-WebRequest $cfgAsset.browser_download_url -OutFile $cfgPath
    }

    Write-Host ""
    Write-Host "Azure App Registration" -ForegroundColor Cyan
    $clientId = Read-Host "  ClientId  (required)"
    $tenantId  = Read-Host "  TenantId  [common]"
    if ([string]::IsNullOrWhiteSpace($tenantId)) { $tenantId = "common" }

    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $cfg.AzureAd.ClientId = $clientId
        $cfg.AzureAd.TenantId = $tenantId
        $cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath
        Write-Host "Config written to: $cfgPath" -ForegroundColor Green
    }
}

# --- PATH ---
$currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($currentPath -notlike "*$dest*") {
    [Environment]::SetEnvironmentVariable("PATH", "$currentPath;$dest", "User")
    $env:PATH += ";$dest"
    Write-Host "Added $dest to PATH" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Run: emaildl" -ForegroundColor Green
