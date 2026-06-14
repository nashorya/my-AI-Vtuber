#!/bin/bash
# publish.sh - Build AIVTuber for Windows x64 distribution
set -e

echo "=== Building AIVTuber for win-x64 ==="

# Publish the App project
dotnet publish App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

PUBLISH_DIR="App/bin/Release/net10.0/win-x64/publish"

echo "=== Copying distribution files ==="

# Copy config template
cp config.json.template "$PUBLISH_DIR/"

# Copy danmaku bridge script
cp danmaku_bridge.py "$PUBLISH_DIR/"

# Copy README
cp README.txt "$PUBLISH_DIR/"

# Create models directory placeholder
mkdir -p "$PUBLISH_DIR/models/bge-small-zh"
echo "# Place bge-small-zh-v1.5 ONNX model files here" > "$PUBLISH_DIR/models/bge-small-zh/README.txt"
echo "# Download from: https://huggingface.co/BAAI/bge-small-zh-v1.5" >> "$PUBLISH_DIR/models/bge-small-zh/README.txt"

echo "=== Distribution package created ==="
echo "Location: $PUBLISH_DIR/"
echo ""
echo "Contents:"
ls -lh "$PUBLISH_DIR/"
echo ""
echo "To distribute: zip the publish/ folder and include config.json.template"
echo "Users should:"
echo "  1. Copy config.json.template to config.json"
echo "  2. Edit config.json with their API keys"
echo "  3. Double-click App.exe to start"