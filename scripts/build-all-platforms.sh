#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "============================================================"
echo "  Rhyolite (.NET 10 + Avalonia 12) Multi-Platform Builder"
echo "============================================================"

echo "[1/6] Running xUnit Test Suite (Rhyolite.Tests)..."
dotnet test tests/Rhyolite.Tests/Rhyolite.Tests.csproj -c Release

echo "[2/6] Publishing macOS (osx-arm64 & osx-x64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r osx-arm64 --no-self-contained -o artifacts/publish/macos-arm64
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r osx-x64 --no-self-contained -o artifacts/publish/macos-x64

echo "[3/6] Publishing Windows (win-x64 & win-arm64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r win-x64 --no-self-contained -o artifacts/publish/win-x64
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r win-arm64 --no-self-contained -o artifacts/publish/win-arm64

echo "[4/6] Publishing Linux (linux-x64 & linux-arm64)..."
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r linux-x64 --no-self-contained -o artifacts/publish/linux-x64
dotnet publish src/Rhyolite.Desktop/Rhyolite.Desktop.csproj -c Release -r linux-arm64 --no-self-contained -o artifacts/publish/linux-arm64

echo "[5/6] Building iOS / iPadOS (net10.0-ios)..."
dotnet build src/Rhyolite.iOS/Rhyolite.iOS.csproj -c Debug

echo "[6/6] Building Android (net10.0-android)..."
dotnet build src/Rhyolite.Android/Rhyolite.Android.csproj -c Debug

echo "============================================================"
echo "  All 5 Platform Families Built Successfully!"
echo "============================================================"
