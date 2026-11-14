#!/bin/bash
# Faz o Proton.app (o que se abre com duplo clique no Mac) a partir do que o dotnet publish deu.
#   make-mac-app.sh <pasta do publish> <pasta de saida> [versao]
# Sem assinatura: o Mac pede "abrir mesmo assim" (botao direito > Abrir) ou  xattr -dr com.apple.quarantine Proton.app
set -euo pipefail
PUB="$1"; OUT="$2"; VER="${3:-0.0.1}"
APP="$OUT/Proton.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB"/. "$APP/Contents/MacOS/"
rm -f "$APP/Contents"/MacOS/*.pdb
chmod +x "$APP/Contents/MacOS/Proton"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Proton</string>
  <key>CFBundleDisplayName</key><string>Proton</string>
  <key>CFBundleIdentifier</key><string>com.atombridge.proton</string>
  <key>CFBundleExecutable</key><string>Proton</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VER</string>
  <key>CFBundleVersion</key><string>$VER</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSRemovableVolumesUsageDescription</key><string>Proton reads the camera cards and copies the files.</string>
</dict>
</plist>
PLIST
echo "$APP"
