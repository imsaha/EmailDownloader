#!/usr/bin/env bash
# EmailDownloader installer for macOS
# Usage: curl -fsSL https://raw.githubusercontent.com/imsaha/EmailDownloader/master/install.sh | bash

set -e

REPO="imsaha/EmailDownloader"
TOOL="emaildl"
DEST="$HOME/.tools/$TOOL"
EXE_PATH="$DEST/$TOOL"
CFG_PATH="$DEST/appsettings.json"
VERSION_FILE="$DEST/version.txt"

# Detect architecture
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    ASSET_NAME="emaildl-osx-arm64"
elif [ "$ARCH" = "x86_64" ]; then
    ASSET_NAME="emaildl-osx-x64"
else
    echo "Unsupported architecture: $ARCH"
    exit 1
fi

# Fetch latest release
RELEASE=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")
LATEST_TAG=$(echo "$RELEASE" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')
DOWNLOAD_URL=$(echo "$RELEASE" | grep '"browser_download_url"' | grep "\"$ASSET_NAME\"" | head -1 | sed 's/.*"browser_download_url": *"\([^"]*\)".*/\1/')
CFG_URL=$(echo "$RELEASE" | grep '"browser_download_url"' | grep 'appsettings\.sample\.json' | head -1 | sed 's/.*"browser_download_url": *"\([^"]*\)".*/\1/')

if [ -z "$DOWNLOAD_URL" ]; then
    echo "Could not find $ASSET_NAME in the latest release of $REPO"
    exit 1
fi

mkdir -p "$DEST"

# --- Exe: update only if version changed ---
INSTALLED_TAG=""
if [ -f "$VERSION_FILE" ]; then
    INSTALLED_TAG=$(cat "$VERSION_FILE" | tr -d '[:space:]')
fi

if [ "$INSTALLED_TAG" = "$LATEST_TAG" ]; then
    echo "$TOOL $LATEST_TAG is already up to date."
else
    if [ -n "$INSTALLED_TAG" ]; then
        echo "Updating $TOOL $INSTALLED_TAG -> $LATEST_TAG..."
    else
        echo "Installing $TOOL $LATEST_TAG..."
    fi
    curl -fsSL "$DOWNLOAD_URL" -o "$EXE_PATH"
    chmod +x "$EXE_PATH"
    echo "$LATEST_TAG" > "$VERSION_FILE"
    echo "Binary updated."
fi

# --- Config: only write on first install, never overwrite ---
if [ -f "$CFG_PATH" ]; then
    echo "Existing config kept: $CFG_PATH"
else
    if [ -n "$CFG_URL" ]; then
        curl -fsSL "$CFG_URL" -o "$CFG_PATH"
    fi

    echo ""
    echo "Azure App Registration"
    read -p "  ClientId  (required): " CLIENT_ID
    read -p "  TenantId  [common]: "   TENANT_ID
    if [ -z "$TENANT_ID" ]; then TENANT_ID="common"; fi

    if [ -f "$CFG_PATH" ]; then
        python3 - <<PYEOF
import json
with open('$CFG_PATH') as f:
    cfg = json.load(f)
cfg['AzureAd']['ClientId'] = '$CLIENT_ID'
cfg['AzureAd']['TenantId'] = '$TENANT_ID'
with open('$CFG_PATH', 'w') as f:
    json.dump(cfg, f, indent=2)
PYEOF
        echo "Config written to: $CFG_PATH"
    fi
fi

# --- PATH ---
add_to_profile() {
    local FILE="$1"
    if [ -f "$FILE" ] && grep -qF "$DEST" "$FILE"; then return; fi
    printf '\n# emaildl\nexport PATH="$PATH:%s"\n' "$DEST" >> "$FILE"
    echo "Added $DEST to PATH in $FILE"
}

ADDED=0
if [ -n "$ZSH_VERSION" ] || [ -f "$HOME/.zshrc" ]; then
    add_to_profile "$HOME/.zshrc" && ADDED=1
fi
if [ -f "$HOME/.bash_profile" ]; then
    add_to_profile "$HOME/.bash_profile" && ADDED=1
elif [ -f "$HOME/.bashrc" ]; then
    add_to_profile "$HOME/.bashrc" && ADDED=1
fi
[ $ADDED -eq 0 ] && add_to_profile "$HOME/.zshrc"

echo ""
echo "Done. Restart your terminal or run:"
echo "  export PATH=\"\$PATH:$DEST\""
echo "Then: emaildl"
