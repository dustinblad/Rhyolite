#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Rhyolite Automated Release & Version Bump Pipeline
# Pattern:
#   - Starts at 0.1.0
#   - Default bump ("patch") increments by +0.0.1 (0.1.0 -> 0.1.1 -> 0.1.2)
#   - Big releases use "minor" (+0.1.0) or "major" (+1.0.0)
#   - "publish" publishes the current version in VERSION without bumping
# ==============================================================================

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

MODE="${1:-patch}"
NOTES_EXTRA="${2:-Automated cross-platform .NET 10 + Avalonia 12 release}"

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

# 3. Build & Package Cross-Platform Release Bundles
RELEASE_DIR="${ROOT_DIR}/artifacts/releases/${TAG}"
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

echo "==> Publishing macOS (osx-arm64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r osx-arm64 --self-contained false -o "${RELEASE_DIR}/osx-arm64" --nologo
(cd "${RELEASE_DIR}/osx-arm64" && zip -qr "../Rhyolite-${TAG}-macos-arm64.zip" .)
rm -rf "${RELEASE_DIR}/osx-arm64"

echo "==> Publishing Windows (win-x64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r win-x64 --self-contained false -o "${RELEASE_DIR}/win-x64" --nologo
(cd "${RELEASE_DIR}/win-x64" && zip -qr "../Rhyolite-${TAG}-win-x64.zip" .)
rm -rf "${RELEASE_DIR}/win-x64"

echo "==> Publishing Linux (linux-x64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r linux-x64 --self-contained false -o "${RELEASE_DIR}/linux-x64" --nologo
tar -czf "${RELEASE_DIR}/Rhyolite-${TAG}-linux-x64.tar.gz" -C "${RELEASE_DIR}/linux-x64" .
rm -rf "${RELEASE_DIR}/linux-x64"

echo "==> Verifying iOS (net10.0-ios) & Android (net10.0-android) compilation..."
dotnet build src/Rhyolite.iOS/Rhyolite.iOS.csproj -c Release --nologo
dotnet build src/Rhyolite.Android/Rhyolite.Android.csproj -c Release --nologo

# Copy App Logo into Release Assets
cp assets/logo/rhyolite-logo.png "${RELEASE_DIR}/rhyolite-logo.png"
cp assets/logo/rhyolite-logo.svg "${RELEASE_DIR}/rhyolite-logo.svg"

# Generate SHA-256 Checksums
(
  cd "$RELEASE_DIR"
  shasum -a 256 Rhyolite-${TAG}-* rhyolite-logo.* > SHA256SUMS.txt
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
RELEASE_BODY="$(cat <<EOF
<p align="center">
  <img src="https://raw.githubusercontent.com/dustinblad/Rhyolite/main/assets/logo/rhyolite-logo.png" width="140" alt="Rhyolite Logo" />
</p>

<h2 align="center">Rhyolite ${TAG}</h2>

<p align="center">
  <strong>High-Speed Local-First Markdown Knowledge Graph, Spatial Canvas & Enterprise DevEx Workbench</strong><br/>
  Built with <strong>C# 14</strong> · <strong>.NET 10</strong> · <strong>Avalonia UI 12</strong> · <strong>Apple Design System</strong>
</p>

---

### Highlights in ${TAG}
- **3-Tier Hybrid Vault Engine**: Plain \`.md\` & \`.canvas\` files (\`~0.14 ms\` atomic writes) + in-memory Markdig AST + **SQLite WAL Sidecar (\`.rhyolite/cache.db\`)** for \`< 25 ms\` cold startup.
- **Enterprise DevEx & GitHub PR Studio**:
  - **1Password CLI (\`op\`) & KeePassXC CLI (\`keepassxc-cli\`)** secret redaction (\`{{ op://Engineering/... }}\`, \`{{ keepassxc://Engineering/... }}\`) & ephemeral runtime injection.
  - **Executable Markdown Runbooks** with destructive command guardrails (\`DROP TABLE\`, \`rm -rf\`, \`kubectl delete\`).
  - **Docs-as-Code Symbol Drift Detector** (\`[[code:path/to/File.cs#Symbol]]\`) & **IDP Gold/Silver/Bronze Scorecards**.
  - **Single-RTT GitHub GraphQL v4 PR Preview**, Vault ADR blast-radius cross-referencing, comment task ingestion, and bidirectional review thread resolution.
- **External Collaborative Workspace Sync**: Bidirectional sync with **[Bladbase Workspaces](https://github.com/dustinblad/bladbase)** (Dual-Authorship Human + AI Agent MCP/Yjs) and **Docmost Spaces**.
- **High-Speed MCP JSON-RPC 2.0 + GraphQL + HLC Merkle P2P Mesh** with Email/Account RBAC and pluggable OAuth2 (\`Enterprise SSO\`, \`Google\`, \`GitHub\`, \`Microsoft\`, \`Apple\`).

### Release Assets & SHA-256 Verification
- \`Rhyolite-${TAG}-macos-arm64.zip\` — macOS Apple Silicon build
- \`Rhyolite-${TAG}-win-x64.zip\` — Windows x64 build
- \`Rhyolite-${TAG}-linux-x64.tar.gz\` — Linux x64 build
- \`rhyolite-logo.png\` & \`rhyolite-logo.svg\` — Official Rhyolite App Icon
- \`SHA256SUMS.txt\` — Cryptographic SHA-256 checksums
EOF
)"

if gh release view "${TAG}" >/dev/null 2>&1; then
  gh release upload "${TAG}" \
    "${RELEASE_DIR}/Rhyolite-${TAG}-macos-arm64.zip" \
    "${RELEASE_DIR}/Rhyolite-${TAG}-win-x64.zip" \
    "${RELEASE_DIR}/Rhyolite-${TAG}-linux-x64.tar.gz" \
    "${RELEASE_DIR}/rhyolite-logo.png" \
    "${RELEASE_DIR}/rhyolite-logo.svg" \
    "${RELEASE_DIR}/SHA256SUMS.txt" \
    --clobber
  gh release edit "${TAG}" --title "Rhyolite ${TAG}" --notes "$RELEASE_BODY"
else
  gh release create "${TAG}" \
    "${RELEASE_DIR}/Rhyolite-${TAG}-macos-arm64.zip" \
    "${RELEASE_DIR}/Rhyolite-${TAG}-win-x64.zip" \
    "${RELEASE_DIR}/Rhyolite-${TAG}-linux-x64.tar.gz" \
    "${RELEASE_DIR}/rhyolite-logo.png" \
    "${RELEASE_DIR}/rhyolite-logo.svg" \
    "${RELEASE_DIR}/SHA256SUMS.txt" \
    --title "Rhyolite ${TAG}" \
    --notes "$RELEASE_BODY"
fi

echo "✓ Successfully published Rhyolite ${TAG} to GitHub Releases!"
