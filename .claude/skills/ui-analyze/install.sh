#!/bin/bash
# Install the ui-analyze skill into a project's .claude/ directory
# Usage: bash install.sh [target_project_path]
#   If no path given, installs to current directory's .claude/

set -e

TARGET="${1:-.}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "Installing ui-analyze skill to: $TARGET/.claude/"

# Create directories
mkdir -p "$TARGET/.claude/commands"
mkdir -p "$TARGET/.claude/scripts"

# Copy files
cp "$SCRIPT_DIR/ui_analyze.py" "$TARGET/.claude/scripts/"
cp "$SCRIPT_DIR/gemini_compare.py" "$TARGET/.claude/scripts/"
cp "$SCRIPT_DIR/ui-analyze.md" "$TARGET/.claude/commands/"

# Update paths in the command file to be relative
echo ""
echo "Installed! Files:"
echo "  $TARGET/.claude/commands/ui-analyze.md"
echo "  $TARGET/.claude/scripts/ui_analyze.py"
echo "  $TARGET/.claude/scripts/gemini_compare.py"
echo ""
echo "Requirements:"
echo "  - GEMINI_API_KEY env var (for Gemini 3.1 Pro)"
echo "  - Python packages: easyocr opencv-python scikit-learn scikit-image scipy"
echo "    Pillow fonttools ultralytics html2image numpy"
echo ""
echo "  pip install easyocr opencv-python scikit-learn scikit-image scipy Pillow fonttools ultralytics html2image"
echo ""
echo "Usage: /ui-analyze <path-to-screenshot> [context]"
