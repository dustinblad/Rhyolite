# Rhyolite — Complete Specification & Prompt Sequence for Claude Code Replication

This document contains the exact chronological user prompts, architectural decisions, and acceptance criteria used to design, build, test, and release **Rhyolite (`v0.1.0`)**. You can feed either the **Single Master Prompt (Section 1)** or the **Step-by-Step Conversational Prompts (Section 2)** into **Claude Code** (or any other agentic coding harness) to replicate Rhyolite from a blank directory and compare the resulting applications side by side.

---

## 1. Single Master Prompt (Copy & Paste into Claude Code)

```markdown
Build "Rhyolite", a cross-platform Obsidian + local Markdown (.md) knowledge base, spatial canvas (.canvas), and Enterprise Developer Experience (DevEx) workbench built on C# 14 and the latest .NET 10.0.

### Core Requirements & Architecture
1. **UI Framework & Design System**:
   - Use **Avalonia UI 12** (`https://avaloniaui.net/`).
   - Use **`https://github.com/feimacode/open-design-agent-kit`** with the **Apple Design System** (`claude-design-systems/apple` and `claude-design-systems/apple-music-ios`).
   - Support dynamic switching between **Apple Light Mode** (`#FFFFFF` / `#F5F5F7` surfaces, `#1D1D1F` text, `#0071E3` action blue) and **Apple True-Black Dark Mode** (`#000000` canvas, `#1C1C1E` / `#272729` elevated cards, `#2997FF` link blue).
   - Provide a CLI exporter (`--export-mockups <dir>`) that renders high-resolution `1440x900` `.png` and `.jpg` screenshots of every screen directly from the live Avalonia visual tree.

2. **3-Tier Hybrid Storage Engine (Sub-Millisecond Updates)**:
   - **Tier 1 (Canonical Source of Truth)**: Plain `.md` and `.canvas` (JSON Canvas 1.0) files on disk (`~0.14 ms` atomic temp-file swap writes, zero lock-in).
   - **Tier 2 (Hot In-Memory AST & Graph Index)**: Incremental Markdig + YAML frontmatter + `[[WikiLink]]` + `![[Embed]]` + `#tag` + Callout + Task + Flashcard parser (`< 0.3 ms`) with automatic vault-wide `[[WikiLink]]` rename refactoring.
   - **Tier 3 (SQLite WAL Sidecar Cache)**: `.rhyolite/cache.db` running in `PRAGMA journal_mode=WAL` (`notes_meta`, `links`, `snapshots`, `github_pr_cache`, `external_sync_bindings` tables) for `< 25 ms` cold startup.

3. **Obsidian Feature Parity + Native Expansions**:
   - **60fps Force-Directed Knowledge Graph (`KnowledgeGraphControl`)**: Custom Skia control with Fruchterman-Reingold physics, iterative **PageRank centrality**, clustering density, orphan detection, and unresolved ghost-link diagnostics.
   - **Infinite Spatial Canvas (`.canvas` JSON Canvas 1.0)**: Custom Skia control with draggable cards, group containers, and Bezier connectors.
   - **Bases Live Frontmatter Database**: Projects YAML frontmatter across vault notes into **Table Views** and **Kanban Boards**.
   - **Native FSRS v4 Spaced Repetition Flashcards**: Parses `Q:/A:`, `::`, and `{{c1::cloze}}` cards from any note and schedules reviews using FSRS v4 stability/difficulty equations.
   - **Local TF-IDF / Cosine RAG AI Copilot**: Synthesizes vault answers with `[[WikiLink]]` citations.
   - **Snapshot Time-Machine & LCS Visual Diff**: Line-by-line Longest Common Subsequence (LCS) diffs against historical snapshots stored in `.rhyolite/cache.db`.
   - **Apple Design Standalone HTML Exporter**.

4. **High-Speed MCP + GraphQL + Peer-to-Peer (P2P) Mesh & Identity/RBAC**:
   - **Model Context Protocol (MCP) JSON-RPC 2.0 Server** (`initialize`, `tools/list`, `tools/call`, `resources/list`, `resources/read`) & **GraphQL Engine** for AI Agent harnesses and peer Rhyolite installations.
   - **Peer-to-Peer (P2P) Mesh (`PeerSyncEngine`)**: Hybrid Logical Clocks (`HlcTimestamp`) + SHA-256 Merkle Tree vault root comparison and 3-way line merge.
   - **Authentication & RBAC (`RhyoliteAuthEngine`)**: Email/Account authentication (`PBKDF2-SHA512` + `HMAC-SHA256` bearer tokens + scoped PATs), 7-tier RBAC (`VaultOwner`, `Admin`, `Editor`, `Reader`, `AiAgentReadWrite`, `AiAgentReadOnly`, `TrustedPeerNode`), folder/`#tag` ACL rules, and pluggable PKCE S256 OAuth2 providers for **Enterprise SSO (OIDC)**, **Google**, **GitHub**, **Microsoft Entra ID**, and **Sign in with Apple**.

5. **External Collaborative Workspace Sync (Bladbase & Docmost)**:
   - **Bladbase Workspace Connector (`https://github.com/dustinblad/bladbase`)**: Bidirectional 3-way sync preserving Human + AI Agent Dual-Authorship (`bladbase_author_type: human | agent`, `bladbase_author_id`, `bladbase_role`, `bladbase_workspace_id`, `bladbase_page_id`) in YAML frontmatter.
   - **Docmost Space Connector (`docmost`)**: Bidirectional sync of Spaces & nested Pages (`/api/spaces`, `/api/pages/*`) with lossless `[[WikiLink]]` <-> `/s/{spaceSlug}/p/{pageSlug}` conversion.

6. **Enterprise Developer Experience (DevEx) & GitHub PR Studio**:
   - **1Password CLI (`op`) & KeePassXC CLI (`keepassxc-cli`) Integration + Shift-Left Secret Scanner/Redactor**: Scans notes and PRs for AWS (`AKIA...`), GitHub (`ghp_...`), Linear (`lin_api_...`), Slack (`xoxb-...`), OpenAI/Anthropic (`sk-...`), and PEM keys; rewrites them to `{{ op://Engineering/<Item>/<Field> }}` or `{{ keepassxc://Engineering/<Item>/<Field> }}`; and resolves them ephemerally in memory via `op read` and `keepassxc-cli show` during runbook execution.
   - **Executable Markdown Runbooks**: Parses fenced `bash`, `sh`, `http`, `sql`, `graphql`, and `pwsh` blocks (` ```bash env=prod-readonly `), injects 1Password/KeePassXC secrets at runtime, and blocks destructive commands (`DROP TABLE`, `rm -rf`, `kubectl delete`).
   - **Docs-as-Code Symbol Drift Detector**: Verifies `[[code:path/to/File.cs#SymbolName]]` anchors against the local repository to catch "Rotten Docs".
   - **Internal Developer Portal (IDP) Operational Maturity Scorecards**: Grades service/ADR/runbook notes into **Gold / Silver / Bronze (`0–100`)** readiness tiers.
   - **Single-RTT GitHub GraphQL v4 PR Preview, Blast-Radius Analyzer & Comment Ingestion Studio**:
     - Builds a single-RTT GitHub GraphQL v4 query fetching PR metadata, CI `statusCheckRollup`, changed file stats, and nested `reviewThreads(first: 100)`, cached in SQLite WAL by `pr_cache:{repo}#{number}:{headRefOid}`.
     - Cross-references modified PR files against vault `[[code:...]]` anchors to highlight **Impacted Vault Docs & ADRs**.
     - Materializes unresolved PR review threads into a structured `.md` note with actionable `- [ ]` tasks and generates bidirectional `addPullRequestReviewThreadReply` + `resolveReviewThread` GraphQL mutations.

7. **Multi-Platform Targets (`.NET 10`)**:
   - Build and verify for **Windows** (`win-x64`), **macOS** (`osx-arm64`), **Linux** (`linux-x64`), **iOS** (`net10.0-ios`), and **Android** (`net10.0-android`).

8. **Multi-Agent Verification, Official App Logo, Tech-Stack `README.md` & `0.1.0` (+`0.0.1`) Release Pipeline**:
   - Use an **Orchestration & Verification Agent** that coordinates specialist agents, audits their code for algorithmic efficiency (`O(1)` lookups, single-RTT queries), runs a 10-suite xUnit test & benchmark project (`Rhyolite.Tests`), and writes `docs/MULTI_AGENT_VERIFICATION_REPORT.md` and `docs/FEATURE_GAP_ANALYSIS.md`.
   - Create an official **Rhyolite App Logo** (`assets/logo/rhyolite-logo.png` & `.svg`) and embed it in the app UI, at the top of `README.md`, and in GitHub Releases.
   - Write a comprehensive `README.md` with official tech-stack logo badges, all 9 rendered app screenshots, quick-start instructions, and simple user/developer documentation.
   - Implement a deterministic **Release Pattern** (`VERSION` + `Directory.Build.props` + `scripts/release.sh` + `.github/workflows/release.yml`) starting at **`0.1.0`** that automatically bumps by **`+0.0.1`** (`0.1.0` -> `0.1.1` -> `0.1.2`) on standard releases or `minor`/`major` on big releases, packages `macos-arm64`, `win-x64`, and `linux-x64` archives + `SHA256SUMS.txt` + `rhyolite-logo.png`, and publishes the GitHub Release.
```

---

## 2. Step-by-Step Conversational Prompt Sequence (As Originally Prompted)

If you want to replay the exact conversational evolution step-by-step:

1. **Step 1**: *"Create a plan, I want to build an Obsidian and local md project cross platform project built on c# and latest version of .net core. 1. use https://avaloniaui.net/ 2. use https://github.com/feimacode/open-design-agent-kit using Apple design 3. Meet Obsidian features and expand on them. 4. Name of the product is called Rhyolite 5. After building everything out, do a feature gap analysis 6. Build for windows, macos, ios, android, and linux"*
2. **Step 2**: *"Updating an .md file should be really fast. Should it use an sqlite backend or is file based ok?"*
3. **Step 3**: *"ok, before we build out the plan, build a several screen mockup on how the app will look using our Apple inspired design."* (Render viewable `.png` and `.jpg` screenshots).
4. **Step 4**: *"launch an agent to also design a feature to the plan to create a highly optimized and speed efficient MCP or GRAPHQL connection that will allow for reading, writing, updates etc. The API can be accessed from AI Agent harnesses or from other Rhyolite installations for a fast Peer to Peer connection. It will handle access and roles via email, accounts for now with the future ability to add SSO, Google, Github, Microsoft, and Apple authentication."*
5. **Step 5**: *"Add to the PLANS the ability to sync with a Docmost Space and Bladbase workspace: https://github.com/dustinblad/bladbase"*
6. **Step 6**: *"Do a feature gap analysis and search the web for something most developers would want to use in an enterprise environment that would help with their development experience."*
7. **Step 7**: *"1. Address this with 1pass cli and KeePass cli integration as well as the scanner and redactor 2. Yes enable and implement this [Executable Runbooks] 3. Yes, integrate and build this [Docs-as-Code Drift Detection] 4. Yes, integrate this [IDP Operational Maturity Scorecards]. 5. Yes, integrate this, and build more efficient integration into PR mainly with Github. Add all these features into the plan. The UI should have integration with Github with PR preview and analyzing and pulling in the comments. Also add the verification & suggested high-efficiency PR features to the plan."*
8. **Step 8**: *"Ok, let's launch several agents to implement this plan including an orchestration agent that all the other agents report to. They need to tell the orchestration agent what they did and the work has to be verified and justified that its work is good and efficient. Before we start working on this init github and push to remote using our product name."*
9. **Step 9**: *"Build a Release Pattern starting with 0.1.0 and bump version by 0.0.1 each time unless its a big release. Build out a well designed README.md file that includes all the features and how to get it to work along with simple documentation. Use logos that the tech stack uses. Also, build out a logo that the app can use and use it in the Releases that we build out and publish to the github project. Use the logo at the Top of the README.md file as well."*
