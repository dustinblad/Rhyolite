#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Rhyolite Automated Release & Version Bump Pipeline
# Pattern:
#   - Starts at 0.1.0
#   - Default bump ("patch") increments by +0.0.1 (0.1.0 -> 0.1.1 -> 0.1.2)
#   - Big releases use "minor" (+0.1.0) or "major" (+1.0.0)
#   - "publish" publishes the current version in VERSION without bumping
#   - Combines all .dlls and native runtime libraries into single-file executables
#   - Embeds Win32 DIB+PNG Rhyolite.ico into Rhyolite.exe & Rhyolite-UserSetup.exe
#   - Builds per-user installers for Windows (.exe), macOS (.dmg/.app), and Linux (.sh)
# ==============================================================================

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

MODE="${1:-patch}"
NOTES_EXTRA="${2:-Single-file executables with embedded icons and user-based installers}"

CURRENT_VERSION="$(tr -d '[:space:]' < VERSION)"
IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT_VERSION"

case "$MODE" in
  publish|current)
    NEXT_VERSION="$CURRENT_VERSION"
    ;;
  patch|+0.0.1)
    PATCH=$((PATCH + 1))
    NEXT_VERSION="${MAJOR}.${MINOR}.${PATCH}"
    ;;
  minor|+0.1.0|big)
    MINOR=$((MINOR + 1))
    PATCH=0
    NEXT_VERSION="${MAJOR}.${MINOR}.${PATCH}"
    ;;
  major|+1.0.0)
    MAJOR=$((MAJOR + 1))
    MINOR=0
    PATCH=0
    NEXT_VERSION="${MAJOR}.${MINOR}.${PATCH}"
    ;;
  set)
    NEXT_VERSION="${2:?Usage: ./scripts/release.sh set <X.Y.Z>}"
    ;;
  *)
    echo "Usage: $0 [publish|patch|minor|major|set <X.Y.Z>] [release_notes]"
    exit 1
    ;;
esac

TAG="v${NEXT_VERSION}"
echo "==> Preparing Rhyolite Release ${TAG} (Mode: ${MODE}, Previous: v${CURRENT_VERSION})"

# 1. Update VERSION and Directory.Build.props if version changed
if [[ "$NEXT_VERSION" != "$CURRENT_VERSION" ]]; then
  echo "${NEXT_VERSION}" > VERSION

  cat > Directory.Build.props <<EOF
<Project>
  <PropertyGroup>
    <!-- Canonical Rhyolite Release Version (starts at 0.1.0, increments by 0.0.1 per release) -->
    <Version>${NEXT_VERSION}</Version>
    <AssemblyVersion>${NEXT_VERSION}.0</AssemblyVersion>
    <FileVersion>${NEXT_VERSION}.0</FileVersion>
    <InformationalVersion>${NEXT_VERSION}</InformationalVersion>
    <ApplicationDisplayVersion>${NEXT_VERSION}</ApplicationDisplayVersion>
    <Product>Rhyolite</Product>
    <Company>Dustin Blad</Company>
    <Authors>Dustin Blad</Authors>
    <Description>Rhyolite — Local-First Markdown Knowledge Graph, Spatial Canvas &amp; Enterprise DevEx Workbench (.NET 10 + Avalonia 12 + Apple Design System)</Description>
    <RepositoryUrl>https://github.com/dustinblad/Rhyolite</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <NoWarn>\$(NoWarn);NU1903</NoWarn>
  </PropertyGroup>
</Project>
EOF

  # Update version badge in README.md
  if [[ -f README.md ]]; then
    sed -i '' "s/version-[0-9]*\.[0-9]*\.[0-9]*-0071E3/version-${NEXT_VERSION}-0071E3/g" README.md || true
  fi
fi

# 2. Run xUnit Verification Suite
echo "==> Running Rhyolite xUnit verification suite..."
dotnet test tests/Rhyolite.Tests/Rhyolite.Tests.csproj -c Release --nologo

# 3. Build & Package Cross-Platform Single-File Executables + User-Based Installers
RELEASE_DIR="${ROOT_DIR}/artifacts/releases/${TAG}"
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# ------------------------------------------------------------------------------
# 3A. macOS Single-File .app Bundle + User-Based .dmg Installer (osx-arm64)
# ------------------------------------------------------------------------------
echo "==> Publishing macOS Single-File Executable & Rhyolite.app Bundle (osx-arm64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj \
  -c Release \
  -r osx-arm64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "${RELEASE_DIR}/osx-arm64" \
  --nologo

APP_BUNDLE="${RELEASE_DIR}/macos-bundle/Rhyolite.app"
mkdir -p "${APP_BUNDLE}/Contents/MacOS" "${APP_BUNDLE}/Contents/Resources"
cp "${RELEASE_DIR}/osx-arm64/Rhyolite" "${APP_BUNDLE}/Contents/MacOS/Rhyolite"
chmod +x "${APP_BUNDLE}/Contents/MacOS/Rhyolite"
cp "src/Rhyolite.Desktop/Rhyolite.icns" "${APP_BUNDLE}/Contents/Resources/Rhyolite.icns"

cat > "${APP_BUNDLE}/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Rhyolite</string>
  <key>CFBundleDisplayName</key>
  <string>Rhyolite</string>
  <key>CFBundleIdentifier</key>
  <string>com.dustinblad.rhyolite</string>
  <key>CFBundleVersion</key>
  <string>${NEXT_VERSION}</string>
  <key>CFBundleShortVersionString</key>
  <string>${NEXT_VERSION}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleExecutable</key>
  <string>Rhyolite</string>
  <key>CFBundleIconFile</key>
  <string>Rhyolite.icns</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
</dict>
</plist>
EOF

codesign --force --deep --sign - "${APP_BUNDLE}" >/dev/null 2>&1 || true

(cd "${RELEASE_DIR}/macos-bundle" && zip -qry "../Rhyolite-${TAG}-macos-arm64.zip" Rhyolite.app)

# Create macOS User-Based .dmg Installer
cat > "${RELEASE_DIR}/macos-bundle/Install-For-Current-User.command" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
USER_APPS="$HOME/Applications"
mkdir -p "$USER_APPS"
rm -rf "$USER_APPS/Rhyolite.app"
cp -R "$SCRIPT_DIR/Rhyolite.app" "$USER_APPS/Rhyolite.app"
xattr -cr "$USER_APPS/Rhyolite.app" 2>/dev/null || true
echo "✓ Installed Rhyolite.app to $USER_APPS/Rhyolite.app (User-Scope, No Admin Required)"
open "$USER_APPS/Rhyolite.app"
EOF
chmod +x "${RELEASE_DIR}/macos-bundle/Install-For-Current-User.command"

if command -v hdiutil >/dev/null 2>&1; then
  hdiutil create -volname "Rhyolite ${TAG}" \
    -srcfolder "${RELEASE_DIR}/macos-bundle" \
    -ov -format UDZO \
    "${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-macos-arm64.dmg" >/dev/null
fi
rm -rf "${RELEASE_DIR}/osx-arm64" "${RELEASE_DIR}/macos-bundle"

# ------------------------------------------------------------------------------
# 3B. Windows Single-File Rhyolite.exe + User-Based Setup .exe (win-x64)
# ------------------------------------------------------------------------------
echo "==> Publishing Windows Single-File Rhyolite.exe (win-x64, all .dlls combined + embedded Rhyolite.ico)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "${RELEASE_DIR}/win-x64" \
  --nologo

(cd "${RELEASE_DIR}/win-x64" && zip -qr "../Rhyolite-${TAG}-win-x64.zip" Rhyolite.exe)

echo "==> Building Windows User-Based Installer (Rhyolite-UserSetup-${TAG}-win-x64.exe)..."
gzip -9 -c "${RELEASE_DIR}/win-x64/Rhyolite.exe" > "${RELEASE_DIR}/win-x64/Rhyolite.exe.gz"
dotnet publish src/Rhyolite.Setup/Rhyolite.Setup.csproj \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PayloadExePath="${RELEASE_DIR}/win-x64/Rhyolite.exe.gz" \
  -o "${RELEASE_DIR}/win-setup" \
  --nologo

mv "${RELEASE_DIR}/win-setup/Rhyolite-UserSetup.exe" "${RELEASE_DIR}/Rhyolite-UserSetup-${TAG}-win-x64.exe"
rm -rf "${RELEASE_DIR}/win-x64" "${RELEASE_DIR}/win-setup"

# ------------------------------------------------------------------------------
# 3C. Linux Single-File Executable + User-Based Installer (linux-x64)
# ------------------------------------------------------------------------------
echo "==> Publishing Linux Single-File Executable & User Installer (linux-x64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj \
  -c Release \
  -r linux-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "${RELEASE_DIR}/linux-x64" \
  --nologo

tar -czf "${RELEASE_DIR}/Rhyolite-${TAG}-linux-x64.tar.gz" -C "${RELEASE_DIR}/linux-x64" Rhyolite

# Create self-extracting Linux user installer script (~/.local/bin/rhyolite)
cat > "${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-linux-x64.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
INSTALL_BIN="$HOME/.local/bin"
INSTALL_APP="$HOME/.local/share/applications"
mkdir -p "$INSTALL_BIN" "$INSTALL_APP"
ARCHIVE_LINE=$(awk '/^__PAYLOAD_BEGINS__/ {print NR + 1; exit 0; }' "$0")
tail -n +"$ARCHIVE_LINE" "$0" | tar -xz -C "$INSTALL_BIN"
mv "$INSTALL_BIN/Rhyolite" "$INSTALL_BIN/rhyolite"
chmod +x "$INSTALL_BIN/rhyolite"
cat > "$INSTALL_APP/rhyolite.desktop" <<DESKTOP
[Desktop Entry]
Name=Rhyolite
Comment=Local-First Markdown Knowledge Graph, Spatial Canvas & Enterprise DevEx Workbench
Exec=$INSTALL_BIN/rhyolite
Type=Application
Categories=Development;Office;Utility;
Terminal=false
DESKTOP
echo "✓ Installed Rhyolite to $INSTALL_BIN/rhyolite (User-Scope, No Sudo Required)"
exit 0
__PAYLOAD_BEGINS__
EOF
cat "${RELEASE_DIR}/Rhyolite-${TAG}-linux-x64.tar.gz" >> "${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-linux-x64.sh"
chmod +x "${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-linux-x64.sh"
rm -rf "${RELEASE_DIR}/linux-x64"

echo "==> Verifying iOS (net10.0-ios) & Android (net10.0-android) compilation..."
dotnet build src/Rhyolite.iOS/Rhyolite.iOS.csproj -c Release --nologo
dotnet build src/Rhyolite.Android/Rhyolite.Android.csproj -c Release --nologo

# Copy App Logo & Icons into Release Assets
cp assets/logo/rhyolite-logo.png "${RELEASE_DIR}/rhyolite-logo.png"
cp assets/logo/rhyolite-logo.svg "${RELEASE_DIR}/rhyolite-logo.svg"
cp assets/logo/rhyolite-logo.ico "${RELEASE_DIR}/rhyolite-logo.ico"

# Generate SHA-256 Checksums
(
  cd "$RELEASE_DIR"
  shasum -a 256 Rhyolite* rhyolite-logo.* > SHA256SUMS.txt
)

# 4. Commit, Tag & Push to GitHub
if [[ -n "$(git status --porcelain)" ]]; then
  git add -A
  git commit -m "chore(release): ${TAG} — ${NOTES_EXTRA}"
fi

git tag -f -a "${TAG}" -m "Rhyolite ${TAG}"
git push origin main
git push origin "${TAG}" --force

# 5. Create / Update GitHub Release with Embedded Logo & Packaged Binaries
RELEASE_NOTES_FILE="${RELEASE_DIR}/RELEASE_NOTES.md"
cat > "$RELEASE_NOTES_FILE" <<EOF
<p align="center">
  <img src="https://raw.githubusercontent.com/dustinblad/Rhyolite/main/assets/logo/rhyolite-logo.png" width="140" alt="Rhyolite Logo" />
</p>

<h2 align="center">Rhyolite ${TAG}</h2>

<p align="center">
  <strong>High-Speed Local-First Markdown Knowledge Graph, Spatial Canvas & Enterprise DevEx Workbench</strong><br/>
  Built with <strong>C# 14</strong> · <strong>.NET 10</strong> · <strong>Avalonia UI 12</strong> · <strong>Apple Design System</strong>
</p>

---

### What's New in ${TAG}
- **Single-File Self-Contained Builds (All \`.dll\`s Combined)**:
  - \`Rhyolite.exe\` (Windows), \`Rhyolite.app\` (macOS), and \`Rhyolite\` (Linux) now bundle all managed \`.dll\` assemblies and native runtime libraries (\`SkiaSharp\`, \`HarfBuzzSharp\`, \`e_sqlite3\`, and the .NET 10 runtime) directly inside a single executable with zero external \`.dll\` files.
- **Embedded Multi-Resolution Win32 PE Icon (\`Rhyolite.ico\`) & Retina \`.icns\`**:
  - \`Rhyolite.exe\` and \`Rhyolite-UserSetup-${TAG}-win-x64.exe\` embed a Win32 DIB+PNG multi-resolution icon (\`16×16\`, \`24×24\`, \`32×32\`, \`48×48\`, \`64×64\`, \`128×128\` 32-bpp BGRA DIB + \`256×256\` PNG) in the PE \`.rsrc\` section (\`RT_GROUP_ICON\` / \`RT_ICON\`) and automatically refresh the Windows Explorer icon cache on install/launch.
- **User-Based Installers (Zero Admin / UAC Required)**:
  - **Windows (\`Rhyolite-UserSetup-${TAG}-win-x64.exe\`)**: Installs per-user to \`%LocalAppData%\\Programs\\Rhyolite\\Rhyolite.exe\` (\`asInvoker\`, no Administrator prompt), creates Start Menu & Desktop shortcuts with the Rhyolite icon, registers \`.md\` and \`.canvas\` file associations under \`HKCU\`, and registers a per-user uninstaller in Windows Installed Apps.
  - **macOS (\`Rhyolite-UserInstaller-${TAG}-macos-arm64.dmg\`)**: Includes \`Rhyolite.app\` with \`Rhyolite.icns\` and a one-click \`Install-For-Current-User.command\` installer to \`~/Applications/Rhyolite.app\`.
  - **Linux (\`Rhyolite-UserInstaller-${TAG}-linux-x64.sh\`)**: Self-extracting per-user installer to \`~/.local/bin/rhyolite\` and \`~/.local/share/applications/rhyolite.desktop\`.

### Release Assets & SHA-256 Verification
- \`Rhyolite-UserSetup-${TAG}-win-x64.exe\` — **Windows x64 Per-User Installer** (No Admin Required)
- \`Rhyolite-${TAG}-win-x64.zip\` — **Windows x64 Portable Single-File \`Rhyolite.exe\`** (All \`.dll\`s Combined)
- \`Rhyolite-UserInstaller-${TAG}-macos-arm64.dmg\` — **macOS Apple Silicon Per-User Disk Image Installer**
- \`Rhyolite-${TAG}-macos-arm64.zip\` — **macOS Apple Silicon \`Rhyolite.app\` Bundle**
- \`Rhyolite-UserInstaller-${TAG}-linux-x64.sh\` — **Linux x64 Per-User Self-Extracting Installer**
- \`Rhyolite-${TAG}-linux-x64.tar.gz\` — **Linux x64 Portable Single-File Executable**
- \`rhyolite-logo.png\`, \`rhyolite-logo.svg\`, \`rhyolite-logo.ico\` — Official Rhyolite App Icons
- \`SHA256SUMS.txt\` — Cryptographic SHA-256 checksums
EOF

UPLOAD_FILES=(
  "${RELEASE_DIR}/Rhyolite-UserSetup-${TAG}-win-x64.exe"
  "${RELEASE_DIR}/Rhyolite-${TAG}-win-x64.zip"
  "${RELEASE_DIR}/Rhyolite-${TAG}-macos-arm64.zip"
  "${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-linux-x64.sh"
  "${RELEASE_DIR}/Rhyolite-${TAG}-linux-x64.tar.gz"
  "${RELEASE_DIR}/rhyolite-logo.png"
  "${RELEASE_DIR}/rhyolite-logo.svg"
  "${RELEASE_DIR}/rhyolite-logo.ico"
  "${RELEASE_DIR}/SHA256SUMS.txt"
)

if [[ -f "${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-macos-arm64.dmg" ]]; then
  UPLOAD_FILES+=("${RELEASE_DIR}/Rhyolite-UserInstaller-${TAG}-macos-arm64.dmg")
fi

if gh release view "${TAG}" >/dev/null 2>&1; then
  gh release upload "${TAG}" "${UPLOAD_FILES[@]}" --clobber
  gh release edit "${TAG}" --title "Rhyolite ${TAG}" --notes-file "$RELEASE_NOTES_FILE"
else
  gh release create "${TAG}" "${UPLOAD_FILES[@]}" --title "Rhyolite ${TAG}" --notes-file "$RELEASE_NOTES_FILE"
fi

echo "✓ Successfully published Rhyolite ${TAG} to GitHub Releases!"
