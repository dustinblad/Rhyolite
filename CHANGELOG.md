# Changelog & Release Pattern

All notable changes to **Rhyolite** are documented in this file.

## Release Versioning Pattern
- **Baseline Release**: Starts at **`0.1.0`**.
- **Standard Incremental Releases (`+0.0.1`)**: Every standard release increments the patch version by `0.0.1` (`0.1.0` → `0.1.1` → `0.1.2` → `0.1.3`) via `./scripts/release.sh patch` (the default).
- **Major / Milestone Feature Releases**: Big architectural releases increment `minor` (`+0.1.0`, e.g., `0.2.0`) or `major` (`+1.0.0`, e.g., `1.0.0`) via `./scripts/release.sh minor` or `./scripts/release.sh major`.

---

## [0.1.0] - 2026-11-03

### Added
- **3-Tier Hybrid Storage Engine (`Rhyolite.Core.Storage`)**:
  - Plain `.md` & `.canvas` files on disk (`~0.14 ms` atomic write via temp-file swap).
  - Hot in-memory Markdig AST & bidirectional link graph (`< 0.3 ms`).
  - SQLite WAL sidecar cache (`.rhyolite/cache.db`) with `notes_meta`, `links`, `snapshots`, `github_pr_cache`, and `external_sync_bindings` tables.
- **Apple Design System (`Rhyolite.UI`)**:
  - Powered by [`feimacode/open-design-agent-kit`](https://github.com/feimacode/open-design-agent-kit) (`apple` & `apple-music-ios`) with pure white Light Mode (`#FFFFFF` / `#F5F5F7`) and OLED True-Black Dark Mode (`#000000` / `#1C1C1E` / `#272729`).
- **Knowledge Graph, Spatial Canvas, Bases & Native Expansions**:
  - 60fps Skia force-directed graph with PageRank centrality, clustering density, and automatic `[[WikiLink]]` rename refactoring.
  - JSON Canvas 1.0 (`.canvas`) spatial board and Bases Table/Kanban database engine.
  - Native **FSRS v4** Spaced Repetition Flashcards, **Local TF-IDF/Cosine RAG Copilot**, and **Snapshot Time-Machine LCS Visual Diffs**.
- **High-Speed MCP, GraphQL, P2P Mesh & Workspace Connectors (`Rhyolite.Core.Protocol`)**:
  - Sub-millisecond JSON-RPC 2.0 MCP server and GraphQL engine with Email/Account RBAC (`PBKDF2-SHA512` + `HMAC-SHA256`) and pluggable PKCE OAuth2 (`Enterprise SSO`, `Google`, `GitHub`, `Microsoft`, `Apple`).
  - Bidirectional sync with **[Bladbase Workspaces](https://github.com/dustinblad/bladbase)** (Dual-Authorship Human + AI Agent MCP/Yjs) and **Docmost Spaces** (`[[WikiLink]]` ↔ ProseMirror AST).
- **Enterprise Developer Experience (DevEx) & GitHub PR Studio (`Rhyolite.Core.Enterprise`)**:
  - Shift-Left DLP Secret Scanner & Redactor with **1Password CLI (`op`)** (`{{ op://Engineering/... }}`) and **KeePassXC CLI (`keepassxc-cli`)** (`{{ keepassxc://Engineering/... }}`) runtime injection.
  - Executable Markdown Runbooks with destructive command guardrails (`DROP TABLE`, `rm -rf`, `kubectl delete`).
  - Docs-as-Code Symbol Drift Detector (`[[code:path/to/File.cs#Symbol]]`) & Internal Developer Portal (IDP) **Gold / Silver / Bronze** Scorecards.
  - Single-RTT GitHub GraphQL v4 PR Preview, Blast-Radius Analyzer, Comment Ingestion & Bidirectional Thread Resolution.
- **Cross-Platform Targets**: Verified builds for **macOS** (`osx-arm64`), **Windows** (`win-x64`), **Linux** (`linux-x64`), **iOS** (`net10.0-ios`), and **Android** (`net10.0-android`).
