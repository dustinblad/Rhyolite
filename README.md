# Rhyolite

**Rhyolite** is a high-speed, local-first Markdown (`.md`) Knowledge Base, Spatial Canvas (`.canvas`), and Enterprise Developer Workbench built with **C# 14**, **.NET 10**, **Avalonia UI 12**, and the **Apple Design System** ([`feimacode/open-design-agent-kit`](https://github.com/feimacode/open-design-agent-kit)).

![Rhyolite Editorial Note Studio (Apple Light Mode)](docs/mockups/01_note_studio_light.png)

## Core Architecture & Features

1. **Zero-Latency 3-Tier Hybrid Storage (`Rhyolite.Core.Storage`)**:
   - **Tier 1 (Canonical Source of Truth)**: Plain `.md` and `.canvas` (JSON Canvas 1.0) files on disk (`~0.14 ms` atomic write via temp-file swap).
   - **Tier 2 (Hot In-Memory AST & Graph Index)**: Incremental Markdig AST + YAML frontmatter + `[[WikiLink]]` delta parser (`< 0.3 ms`).
   - **Tier 3 (SQLite WAL Sidecar Cache)**: `.rhyolite/cache.db` running in `PRAGMA journal_mode=WAL` for `< 25 ms` cold startup, FTS indexing, Snapshot Time-Machine diffs, and GitHub PR `ETag` / `headRefOid` caching.
2. **Apple Design System (`Rhyolite.UI`)**:
   - Built on `open-design-agent-kit` (`apple` & `apple-music-ios`): Pure white (`#FFFFFF` / `#F5F5F7`) Light Mode and OLED True-Black (`#000000` / `#1C1C1E` / `#272729`) Dark Mode with `#0071E3` / `#2997FF` accents.
3. **Obsidian Parity + Native Expansions**:
   - **60fps Skia Force-Directed Knowledge Graph** with iterative PageRank centrality, clustering density, orphan/ghost link diagnostics, and automatic vault-wide `[[WikiLink]]` rename refactoring.
   - **Infinite Spatial Canvas (`.canvas` JSON Canvas 1.0)** & **Bases Live Frontmatter Database** (Table & Kanban Views).
   - **Native FSRS v4 Spaced Repetition Flashcards** (`Q:/A:`, `::`, `{{c1::cloze}}`), **Local TF-IDF/Cosine RAG AI Copilot**, and **Snapshot Time-Machine LCS Visual Diffs**.
4. **High-Speed MCP + GraphQL + Peer-to-Peer (P2P) Mesh & RBAC/OAuth (`Rhyolite.Core.Protocol`)**:
   - Sub-millisecond **Model Context Protocol (MCP)** JSON-RPC 2.0 server and **GraphQL** engine for AI Agent harnesses and peer Rhyolite installations.
   - **Hybrid Logical Clocks (HLC)** + **SHA-256 Merkle Tree** P2P delta synchronization.
   - **Email/Account RBAC** (`PBKDF2-SHA512` + `HMAC-SHA256`) + pluggable PKCE S256 OAuth2 for **Enterprise SSO (OIDC)**, **Google**, **GitHub**, **Microsoft Entra ID**, and **Sign in with Apple**.
   - **External Collaborative Workspace Connectors**: Bidirectional sync with **[Bladbase Workspaces](https://github.com/dustinblad/bladbase)** (Dual-Authorship Human + AI Agent MCP/Yjs) and **Docmost Spaces** (`[[WikiLink]]` ↔ ProseMirror AST).
5. **Enterprise Developer Experience (DevEx) & GitHub PR Studio (`Rhyolite.Core.Enterprise`)**:
   - **Shift-Left DLP Secret Scanner & Redactor** with native **1Password CLI (`op`)** (`{{ op://Engineering/... }}`) and **KeePassXC CLI (`keepassxc-cli`)** (`{{ keepassxc://Engineering/... }}`) ephemeral runtime injection.
   - **Executable Markdown Runbooks** (`bash`, `sh`, `http`, `sql`, `graphql`, `pwsh`) with destructive command guardrails (`DROP TABLE`, `rm -rf`, `kubectl delete`).
   - **Docs-as-Code Symbol Drift Detector** (`[[code:path/to/File.cs#Symbol]]`) & **Internal Developer Portal (IDP) Gold/Silver/Bronze Scorecards**.
   - **Single-RTT GitHub GraphQL v4 PR Preview, Blast-Radius Analyzer & Comment Ingestion** with bidirectional review thread resolution from Markdown.

## Cross-Platform Targets (`.NET 10`)
- **macOS** (`osx-arm64`, `osx-x64`)
- **Windows** (`win-x64`, `win-arm64`)
- **Linux** (`linux-x64`, `linux-arm64`)
- **iOS & iPadOS** (`net10.0-ios`)
- **Android** (`net10.0-android`)
