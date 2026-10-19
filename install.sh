#!/bin/sh
# clibridge4unity installer for Linux (x64)
# Usage: curl -fsSL https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.sh | sh
#   or:  sh install.sh               # install latest
#   or:  sh install.sh 1.1.87        # install specific version
set -eu

repo="oddgames/clibridge4unity"
asset="clibridge4unity-linux-x64"
install_dir="${CLIBRIDGE_INSTALL_DIR:-$HOME/.local/bin}"
version="${1:-}"

echo "clibridge4unity installer"
echo

if [ -z "$version" ]; then
    echo "Fetching latest release..."
    version=$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
        | sed -n 's/.*"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' | head -n 1)
    if [ -z "$version" ]; then
        echo "Error: Could not fetch latest release from GitHub." >&2
        echo "       Check your internet connection or pass a version: sh install.sh 1.1.86" >&2
        exit 1
    fi
fi

url="https://github.com/$repo/releases/download/v$version/$asset"
echo "Version:  $version"
echo "Install:  $install_dir"
echo

mkdir -p "$install_dir"
tmp="$install_dir/clibridge4unity.update"
echo "Downloading $asset..."
if ! curl -fSL --progress-bar "$url" -o "$tmp"; then
    rm -f "$tmp"
    echo "Error: Download failed." >&2
    echo "       URL: $url" >&2
    echo "       Ensure release 'v$version' exists with asset '$asset'." >&2
    exit 1
fi
chmod 755 "$tmp"
mv -f "$tmp" "$install_dir/clibridge4unity"

echo
"$install_dir/clibridge4unity" --version
echo
echo "Installation complete!"
case ":$PATH:" in
    *":$install_dir:"*) ;;
    *)
        echo
        echo "$install_dir is not on your PATH. Add it with:"
        echo "  echo 'export PATH=\"$install_dir:\$PATH\"' >> ~/.bashrc && . ~/.bashrc"
        ;;
esac
echo
echo "Next steps:"
echo "  1. cd to your Unity project"
echo "  2. Run: clibridge4unity SETUP"
echo
echo "This will install the Unity package and set up CLAUDE.md for your project."
