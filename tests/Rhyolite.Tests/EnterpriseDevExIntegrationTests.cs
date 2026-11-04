using System.Diagnostics;
using System.Text.Json.Nodes;
using Rhyolite.Core.Design;
using Rhyolite.Core.Enterprise;
using Rhyolite.Core.Models;
using Rhyolite.Core.Parsing;
using Rhyolite.Core.Protocol;
using Rhyolite.Core.Storage;
using Xunit;

namespace Rhyolite.Tests;

public sealed class EnterpriseDevExIntegrationTests
{
    [Fact]
    public async Task OnePasswordCli_And_KeePassXcCli_SecretRedaction_And_EphemeralResolution_Benchmark()
    {
        const string notePath = "04 — Runbooks/MultiCloud-Incident-Triage.md";
        string fakeAwsKey = "AKIA" + "IOSFODNN7EXAMPLE";
        string fakeGhPat = "ghp_" + "A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
        string fakeLinearKey = "lin_api_" + "9876543210abcdefghijKLMNOPQRST";
        string fakeSlackToken = "xoxb" + "-123456789012-987654321098-AbCdEfGhIjKlMnOpQrStUvWx";
        string rawMarkdownWithSecrets = $$"""
            ---
            owner: "@platform-security"
            tier: tier-1
            ---
            # Multi-Cloud Production Incident Triage
            ```bash env=prod-incident
            export AWS_ACCESS_KEY_ID="{{fakeAwsKey}}"
            export GH_ENTERPRISE_PAT="{{fakeGhPat}}"
            export LINEAR_TOKEN="{{fakeLinearKey}}"
            export SLACK_INCIDENT_TOKEN="{{fakeSlackToken}}"
            ```
            """;

        // Warmup compiled regex & async state machine once so benchmark reflects steady-state execution
        _ = EnterpriseDevExEngine.ScanAndRedactSecrets(notePath, rawMarkdownWithSecrets, SecretVaultBackend.OnePasswordCli);
        _ = await EnterpriseDevExEngine.ResolveSecretReferenceAsync("{{ op://Engineering/AWS-Production/access_key_id }}", allowLiveCliExecution: false);

        var sw = Stopwatch.StartNew();

        // 1. Scan & redact into 1Password CLI ({{ op://Engineering/... }}) secret references
        var opScan = EnterpriseDevExEngine.ScanAndRedactSecrets(
            notePath,
            rawMarkdownWithSecrets,
            SecretVaultBackend.OnePasswordCli,
            vaultName: "Engineering");

        // 2. Scan & redact into KeePassXC CLI ({{ keepassxc://Engineering/... }}) secret references
        var kpScan = EnterpriseDevExEngine.ScanAndRedactSecrets(
            notePath,
            rawMarkdownWithSecrets,
            SecretVaultBackend.KeePassXcCli,
            vaultName: "Engineering");

        // 3. Resolve ephemeral secret references via 1Password CLI (`op read`) and KeePassXC CLI (`keepassxc-cli show`)
        var opAwsResolution = await EnterpriseDevExEngine.ResolveSecretReferenceAsync(
            "{{ op://Engineering/AWS-Production/access_key_id }}",
            allowLiveCliExecution: false);

        var kpGhResolution = await EnterpriseDevExEngine.ResolveSecretReferenceAsync(
            "{{ keepassxc://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}",
            keepassDatabasePath: "~/.secrets/engineering.kdbx",
            allowLiveCliExecution: false);

        sw.Stop();

        // Verify all 4 secret types (AWS, GitHub, Linear, Slack) were detected & redacted for 1Password CLI
        Assert.False(opScan.IsClean);
        Assert.Equal(SecretVaultBackend.OnePasswordCli, opScan.PreferredBackend);
        Assert.Equal(4, opScan.Findings.Count);
        Assert.Contains(opScan.Findings, f => f.RuleId == "SEC-AWS-01" && f.AppliedPlaceholder == "{{ op://Engineering/AWS-Production/access_key_id }}");
        Assert.Contains(opScan.Findings, f => f.RuleId == "SEC-GH-01" && f.AppliedPlaceholder == "{{ op://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}");
        Assert.Contains(opScan.Findings, f => f.RuleId == "SEC-LIN-01" && f.AppliedPlaceholder == "{{ op://Engineering/Linear-Workspace/LINEAR_API_KEY }}");
        Assert.Contains(opScan.Findings, f => f.RuleId == "SEC-SLACK-01" && f.AppliedPlaceholder == "{{ op://Engineering/Slack-Incident-Bot/SLACK_BOT_TOKEN }}");

        Assert.Contains("{{ op://Engineering/AWS-Production/access_key_id }}", opScan.RedactedMarkdown);
        Assert.Contains("{{ op://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}", opScan.RedactedMarkdown);
        Assert.Contains("{{ op://Engineering/Linear-Workspace/LINEAR_API_KEY }}", opScan.RedactedMarkdown);
        Assert.Contains("{{ op://Engineering/Slack-Incident-Bot/SLACK_BOT_TOKEN }}", opScan.RedactedMarkdown);
        Assert.DoesNotContain(fakeAwsKey, opScan.RedactedMarkdown);
        Assert.DoesNotContain(fakeGhPat, opScan.RedactedMarkdown);
        Assert.DoesNotContain(fakeLinearKey, opScan.RedactedMarkdown);
        Assert.DoesNotContain(fakeSlackToken, opScan.RedactedMarkdown);

        // Verify all 4 secret types were detected & redacted into KeePassXC CLI URIs
        Assert.False(kpScan.IsClean);
        Assert.Equal(SecretVaultBackend.KeePassXcCli, kpScan.PreferredBackend);
        Assert.Equal(4, kpScan.Findings.Count);
        Assert.Contains("{{ keepassxc://Engineering/AWS-Production/access_key_id }}", kpScan.RedactedMarkdown);
        Assert.Contains("{{ keepassxc://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}", kpScan.RedactedMarkdown);
        Assert.Contains("{{ keepassxc://Engineering/Linear-Workspace/LINEAR_API_KEY }}", kpScan.RedactedMarkdown);
        Assert.Contains("{{ keepassxc://Engineering/Slack-Incident-Bot/SLACK_BOT_TOKEN }}", kpScan.RedactedMarkdown);

        // Verify exact 1Password CLI (`op read`) and KeePassXC CLI (`keepassxc-cli show`) command construction
        Assert.Equal(SecretVaultBackend.OnePasswordCli, opAwsResolution.Backend);
        Assert.Equal("op", opAwsResolution.CliCommand.Executable);
        Assert.Equal("read \"op://Engineering/AWS-Production/access_key_id\" --no-newline", opAwsResolution.CliCommand.Arguments);

        Assert.Equal(SecretVaultBackend.KeePassXcCli, kpGhResolution.Backend);
        Assert.Equal("keepassxc-cli", kpGhResolution.CliCommand.Executable);
        Assert.Equal("show --quiet -s -a Password \"~/.secrets/engineering.kdbx\" \"Engineering/GitHub-Enterprise/GITHUB_TOKEN\"", kpGhResolution.CliCommand.Arguments);

        // Verify sub-25ms benchmark budget
        Assert.True(
            sw.Elapsed.TotalMilliseconds < 25.0,
            $"Expected secret scan, dual-backend redaction, and CLI resolution benchmark to complete in < 25 ms, but took {sw.Elapsed.TotalMilliseconds:F3} ms.");
    }

    [Fact]
    public void GitHubPrSingleRttGraphQl_SqliteWalCacheKey_BlastRadius_And_BidirectionalThreadResolution_WorkEndToEnd()
    {
        // 1. Verify Single-RTT GitHub GraphQL v4 query generation
        string graphQlQuery = EnterpriseDevExEngine.BuildSingleRttPullRequestGraphQlQuery("dustinblad", "rhyolite", 142);
        Assert.Contains("query GetPullRequestSubgraph", graphQlQuery);
        Assert.Contains("repository(owner: \"dustinblad\", name: \"rhyolite\")", graphQlQuery);
        Assert.Contains("pullRequest(number: 142)", graphQlQuery);
        Assert.Contains("statusCheckRollup { state }", graphQlQuery);
        Assert.Contains("files(first: 100)", graphQlQuery);
        Assert.Contains("reviewThreads(first: 100)", graphQlQuery);

        // 2. Verify deterministic SQLite WAL sidecar cache key normalization
        var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
        string walCacheKey = EnterpriseDevExEngine.ComputePrSqliteWalCacheKey("DustinBlad/Rhyolite", samplePr.Number, "9F4B2C8E1A7D305B");
        Assert.Equal("pr_cache:dustinblad/rhyolite#142:9f4b2c8e1a7d305b", walCacheKey);

        // 3. Verify Architectural Blast-Radius cross-referencing against Vault ADR notes & blocker task extraction
        var storageAdrNote = new VaultNote
        {
            Id = "adr-001.md",
            RelativePath = "01 — Architecture/ADR-001 Hybrid SQLite WAL Storage.md",
            FullPath = "/tmp/adr-001.md",
            Title = "ADR-001 Hybrid SQLite WAL Storage",
            FolderPath = "01 — Architecture",
            RawMarkdown = """
                ---
                kind: ADR
                status: Accepted
                ---
                # ADR-001 Hybrid SQLite WAL Storage
                Governs atomic writes in [[code:src/Rhyolite.Core/Storage/HybridVaultEngine.cs#SaveNoteAsync]].
                """
        };

        var securityAdrNote = new VaultNote
        {
            Id = "adr-002.md",
            RelativePath = "01 — Architecture/ADR-002 Shift-Left Secret Redaction.md",
            FullPath = "/tmp/adr-002.md",
            Title = "ADR-002 Shift-Left Secret Redaction",
            FolderPath = "01 — Architecture",
            RawMarkdown = """
                ---
                kind: ADR
                status: Accepted
                ---
                # ADR-002 Shift-Left Secret Redaction
                Enforces 1Password CLI and KeePassXC CLI secret injection in `EnterpriseDevExEngine`.
                """
        };

        var unrelatedNote = new VaultNote
        {
            Id = "unrelated.md",
            RelativePath = "02 — Notes/Typography Guidelines.md",
            FullPath = "/tmp/unrelated.md",
            Title = "Typography Guidelines",
            FolderPath = "02 — Notes",
            RawMarkdown = "# Typography Guidelines\nSF Pro and JetBrains Mono font pairings."
        };

        var analysis = EnterpriseDevExEngine.AnalyzeGitHubPullRequest(
            samplePr,
            [storageAdrNote, securityAdrNote, unrelatedNote]);

        Assert.Equal(441, analysis.TotalAdditions);
        Assert.Equal(20, analysis.TotalDeletions);
        Assert.Equal(2, analysis.UnresolvedThreadsCount);
        Assert.Equal(1, analysis.ActionableBlockersCount);
        Assert.True(analysis.PassedDiffSecretScan);
        Assert.Equal("High (Unresolved Blockers / Security Check)", analysis.RiskLevel);

        Assert.Contains("ADR-001 Hybrid SQLite WAL Storage", analysis.ImpactedVaultNotes);
        Assert.Contains("ADR-002 Shift-Left Secret Redaction", analysis.ImpactedVaultNotes);
        Assert.DoesNotContain("Typography Guidelines", analysis.ImpactedVaultNotes);

        Assert.Equal(2, analysis.ExtractedReviewTasks.Count);
        Assert.Contains(
            analysis.ExtractedReviewTasks,
            t => t.StartsWith("- [ ] **@staff-sec-reviewer** (src/Rhyolite.Core/Enterprise/EnterpriseDevExEngine.cs:94):", StringComparison.Ordinal)
                 && t.EndsWith("⏫ #pr-blocker", StringComparison.Ordinal));
        Assert.Contains(
            analysis.ExtractedReviewTasks,
            t => t.StartsWith("- [ ] **@perf-lead** (src/Rhyolite.Core/Storage/HybridVaultEngine.cs:52):", StringComparison.Ordinal)
                 && t.EndsWith("#pr-review", StringComparison.Ordinal));

        Assert.Contains("[[ADR-001 Hybrid SQLite WAL Storage]]", analysis.GeneratedReviewMarkdown);
        Assert.Contains("[[ADR-002 Shift-Left Secret Redaction]]", analysis.GeneratedReviewMarkdown);
        Assert.Contains("- [ ] **@staff-sec-reviewer**", analysis.GeneratedReviewMarkdown);

        // 4. Verify Bidirectional PR Thread Resolution GraphQL v4 Mutation
        string mutation = EnterpriseDevExEngine.BuildBidirectionalPrThreadResolutionMutation(
            "PRRT_kwDO101",
            "Verified \"ephemeral\" 1Password/KeePassXC injection in commit 9f4b2c8.");
        Assert.Contains("mutation ResolveAndReplyPullRequestThread", mutation);
        Assert.Contains("pullRequestReviewThreadId: \"PRRT_kwDO101\"", mutation);
        Assert.Contains("body: \"Verified \\\"ephemeral\\\" 1Password/KeePassXC injection in commit 9f4b2c8.\"", mutation);
        Assert.Contains("resolveReviewThread(input: { threadId: \"PRRT_kwDO101\" })", mutation);
    }

    [Fact]
    public async Task BladbaseWorkspace_DualAuthorship_And_DocmostSpace_ThreeWayMerge_PreserveAllEdits()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteThreeWayMergeTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var vault = new HybridVaultEngine(tempDir);
            await vault.LoadOrSeedVaultAsync();

            var connectors = new WorkspaceConnectorsEngine();

            // 1. Seed a local Rhyolite note in "01 — Architecture" with local-only human edits
            const string bladbaseNotePath = "01 — Architecture/Bladbase CRDT Mesh Spec.md";
            const string localBladbaseMarkdown = """
                ---
                owner: "@dustinblad"
                ---
                # Bladbase CRDT Mesh Spec
                - Shared Architecture Invariant: Local .md files remain canonical on disk.
                - Local Rhyolite Human Edit: Added sub-millisecond SQLite WAL snapshot trigger.
                """;
            await vault.SaveNoteAsync(bladbaseNotePath, localBladbaseMarkdown, "Local Human Edit");

            // Remote Bladbase (dustinblad/bladbase) page has a non-overlapping AI Agent edit
            var remoteBladbasePages = new[]
            {
                new BladbasePageDocument(
                    PageId: "bb-page-merge-01",
                    WorkspaceId: "ws-bladbase-core",
                    ParentPageId: "bb-root-arch",
                    Title: "Bladbase CRDT Mesh Spec",
                    MarkdownBody: """
                        # Bladbase CRDT Mesh Spec
                        - Shared Architecture Invariant: Local .md files remain canonical on disk.
                        - Remote Bladbase AI Agent Edit: Synchronized Yjs state vector & MCP tool schema from dustinblad/bladbase.
                        """,
                    AuthorKind: WorkspaceAuthorKind.AiAgent,
                    AuthorId: "bladbase-mcp-agent",
                    WorkspaceRole: "maintainer",
                    ContentSha256: "sha256-remote-bb-01",
                    UpdatedAtUtc: DateTimeOffset.UtcNow)
            };

            var bbMergeSummary = await connectors.SyncBladbaseWorkspaceAsync(vault, "ws-bladbase-core", remoteBladbasePages);
            Assert.Equal(ExternalConnectorKind.BladbaseWorkspace, bbMergeSummary.ConnectorKind);
            Assert.Equal(1, bbMergeSummary.MergedCount);

            var mergedBbNote = vault.Notes.First(n => n.Title == "Bladbase CRDT Mesh Spec");
            MarkdownAstEngine.ParseNoteMetadata(mergedBbNote);

            // Confirm Dual-Authorship frontmatter and zero data loss across both local & remote edits
            Assert.Equal("ws-bladbase-core", mergedBbNote.GetProperty("bladbase_workspace_id"));
            Assert.Equal("bb-page-merge-01", mergedBbNote.GetProperty("bladbase_page_id"));
            Assert.Equal("bb-root-arch", mergedBbNote.GetProperty("bladbase_parent_id"));
            Assert.Equal("agent", mergedBbNote.GetProperty("bladbase_author_type"));
            Assert.Equal("bladbase-mcp-agent", mergedBbNote.GetProperty("bladbase_author_id"));
            Assert.Contains("- Shared Architecture Invariant: Local .md files remain canonical on disk.", mergedBbNote.BodyWithoutFrontmatter);
            Assert.Contains("- Local Rhyolite Human Edit: Added sub-millisecond SQLite WAL snapshot trigger.", mergedBbNote.BodyWithoutFrontmatter);
            Assert.Contains("- Remote Bladbase AI Agent Edit: Synchronized Yjs state vector & MCP tool schema from dustinblad/bladbase.", mergedBbNote.BodyWithoutFrontmatter);

            // 2. Seed a local Rhyolite note for Docmost Space 3-way merge
            const string docmostNotePath = "01 — Architecture/Docmost Knowledge Federation.md";
            const string localDocmostMarkdown = """
                ---
                owner: "@core-docs"
                ---
                # Docmost Knowledge Federation
                - Shared Link: References [[Storage Architecture]] for persistence guarantees.
                - Local Rhyolite Engineer Edit: Verified offline-first Markdown AST round-trip fidelity.
                """;
            await vault.SaveNoteAsync(docmostNotePath, localDocmostMarkdown, "Local Engineer Edit");

            // Remote Docmost page has ProseMirror internal links (/s/engineering/p/...) and a non-overlapping edit
            var remoteDocmostPages = new[]
            {
                new DocmostPageDocument(
                    PageId: "dm-page-merge-02",
                    SpaceId: "spc-docmost-arch",
                    SpaceSlug: "engineering",
                    ParentPageId: "dm-parent-01",
                    Slug: "docmost-knowledge-federation",
                    Title: "Docmost Knowledge Federation",
                    MarkdownContent: """
                        # Docmost Knowledge Federation
                        - Shared Link: References [Storage Architecture](/s/engineering/p/storage-architecture) for persistence guarantees.
                        - Remote Docmost Team Edit: Linked [Avalonia Rendering Pipeline](/s/engineering/p/avalonia-rendering-pipeline) from Docmost Space.
                        """,
                    ContentSha256: "sha256-remote-dm-02",
                    UpdatedAtUtc: DateTimeOffset.UtcNow)
            };

            var dmMergeSummary = await connectors.SyncDocmostSpaceAsync(vault, "spc-docmost-arch", remoteDocmostPages);
            Assert.Equal(ExternalConnectorKind.DocmostSpace, dmMergeSummary.ConnectorKind);
            Assert.Equal(1, dmMergeSummary.MergedCount);

            var mergedDmNote = vault.Notes.First(n => n.Title == "Docmost Knowledge Federation");
            MarkdownAstEngine.ParseNoteMetadata(mergedDmNote);

            // Confirm Docmost Space metadata, ProseMirror -> [[WikiLink]] normalization, and zero data loss
            Assert.Equal("spc-docmost-arch", mergedDmNote.GetProperty("docmost_space_id"));
            Assert.Equal("engineering", mergedDmNote.GetProperty("docmost_space_slug"));
            Assert.Equal("dm-page-merge-02", mergedDmNote.GetProperty("docmost_page_id"));
            Assert.Equal("dm-parent-01", mergedDmNote.GetProperty("docmost_parent_id"));
            Assert.Contains("- Shared Link: References [[Storage Architecture]] for persistence guarantees.", mergedDmNote.BodyWithoutFrontmatter);
            Assert.Contains("- Local Rhyolite Engineer Edit: Verified offline-first Markdown AST round-trip fidelity.", mergedDmNote.BodyWithoutFrontmatter);
            Assert.Contains("- Remote Docmost Team Edit: Linked [[Avalonia Rendering Pipeline]] from Docmost Space.", mergedDmNote.BodyWithoutFrontmatter);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MultiDesignSystemCatalog_SqlitePreferences_McpAndGraphQl_WorkEndToEnd()
    {
        // 1. Verify DesignSystemCatalog.All contains all 9 popular feimacode/open-design-agent-kit design systems
        string[] expectedIds =
        [
            "apple",
            "linear-app",
            "notion",
            "vercel",
            "claude",
            "github",
            "raycast",
            "stripe",
            "supabase"
        ];

        Assert.Equal(9, DesignSystemCatalog.All.Count);
        Assert.Equal(expectedIds, DesignSystemCatalog.All.Select(p => p.Id).ToArray());

        foreach (var preset in DesignSystemCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Category));
            Assert.Contains(preset.Id, preset.DesignMdSourcePath, StringComparison.OrdinalIgnoreCase);

            foreach (var palette in new[] { preset.LightPalette, preset.DarkPalette })
            {
                string[] hexTokens =
                [
                    palette.BgHex,
                    palette.SurfaceHex,
                    palette.SurfaceWarmHex,
                    palette.SurfaceElevatedHex,
                    palette.FgHex,
                    palette.Fg2Hex,
                    palette.MutedHex,
                    palette.MetaHex,
                    palette.BorderHex,
                    palette.BorderSoftHex,
                    palette.AccentHex,
                    palette.AccentHoverHex,
                    palette.LinkHex,
                    palette.CalloutBgHex,
                    palette.SuccessHex,
                    palette.WarnHex,
                    palette.DangerHex
                ];

                foreach (var hex in hexTokens)
                {
                    Assert.StartsWith("#", hex, StringComparison.Ordinal);
                    Assert.True(hex.Length is 7 or 9, $"Invalid hex token '{hex}' in preset '{preset.Id}'.");
                }
            }
        }

        // 2. Benchmark cycling forward (GetNext) 9 times and backward (GetPrevious) 9 times in < 2 ms
        _ = DesignSystemCatalog.GetNext("apple");
        _ = DesignSystemCatalog.GetPrevious("apple");

        var cycleSw = Stopwatch.StartNew();
        var current = DesignSystemCatalog.Default;
        var visitedForward = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 9; i++)
        {
            current = DesignSystemCatalog.GetNext(current.Id);
            visitedForward.Add(current.Id);
        }
        Assert.Equal("apple", current.Id);
        Assert.Equal(9, visitedForward.Count);

        for (int i = 0; i < 9; i++)
        {
            current = DesignSystemCatalog.GetPrevious(current.Id);
        }
        cycleSw.Stop();

        Assert.Equal("apple", current.Id);
        Assert.True(
            cycleSw.Elapsed.TotalMilliseconds < 2.0,
            $"Expected 18 O(1) design system cycle lookups to complete in < 2 ms, but took {cycleSw.Elapsed.TotalMilliseconds:F4} ms.");

        // 3. Verify HybridVaultEngine SQLite WAL ui_preferences persistence (.rhyolite/cache.db)
        //    and 4. Verify McpServerEngine (rhyolite.list_design_systems & rhyolite.set_design_system) + GraphQlEngine (designSystems)
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteDesignPrefTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var vault = new HybridVaultEngine(tempDir);
            await vault.SaveUiPreferenceAsync("active_design_system", "linear-app");
            string? persistedPreset = await vault.GetUiPreferenceAsync("active_design_system");
            Assert.Equal("linear-app", persistedPreset);

            var auth = new RhyoliteAuthEngine();
            var peerEngine = new PeerSyncEngine("node-design-test");
            var mcp = new McpServerEngine(auth, peerEngine, vault);
            var gql = new GraphQlEngine(auth, peerEngine, mcp, vault);

            _ = auth.RegisterAccount("designer@rhyolite.dev", "Design Lead", "StrongPass!2026", VaultRole.VaultOwner);
            var token = auth.LoginWithEmailPassword("designer@rhyolite.dev", "StrongPass!2026");
            Assert.NotNull(token);

            string listRpc = """
                {
                  "jsonrpc": "2.0",
                  "id": 401,
                  "method": "tools/call",
                  "params": {
                    "name": "rhyolite.list_design_systems",
                    "arguments": {}
                  }
                }
                """;
            string listResp = mcp.HandleJsonRpc(listRpc, token!.RawBearerToken);
            Assert.DoesNotContain("\"error\"", listResp, StringComparison.OrdinalIgnoreCase);
            foreach (var expectedId in expectedIds)
            {
                Assert.Contains(expectedId, listResp, StringComparison.OrdinalIgnoreCase);
            }

            string setRpc = """
                {
                  "jsonrpc": "2.0",
                  "id": 402,
                  "method": "tools/call",
                  "params": {
                    "name": "rhyolite.set_design_system",
                    "arguments": {
                      "designId": "claude",
                      "id": "claude",
                      "presetId": "claude"
                    }
                  }
                }
                """;
            string setResp = mcp.HandleJsonRpc(setRpc, token.RawBearerToken);
            Assert.DoesNotContain("\"error\"", setResp, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("claude", setResp, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("claude", await vault.GetUiPreferenceAsync("active_design_system"));

            var gqlResp = gql.Execute(
                new GraphQlRequest("query { designSystems { id name category } }"),
                token.RawBearerToken);
            Assert.False(gqlResp.HasErrors);
            Assert.NotNull(gqlResp.Data);

            var designSystemsArray = gqlResp.Data!["designSystems"] as JsonArray;
            Assert.NotNull(designSystemsArray);
            Assert.Equal(9, designSystemsArray!.Count);
            string serializedGqlData = designSystemsArray.ToJsonString();
            foreach (var expectedId in expectedIds)
            {
                Assert.Contains(expectedId, serializedGqlData, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SqliteWalPrCache_SyncBindings_And_McpGraphQlDevExEndpoints_Benchmark()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteWalMcpGqlTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var vault = new HybridVaultEngine(tempDir);
            vault.EnsureSqliteSchema();

            // 1. Verify SQLite WAL github_pr_cache O(1) B-tree primary key upsert & lookup
            string prKey = EnterpriseDevExEngine.ComputePrSqliteWalCacheKey("dustinblad/rhyolite", 142, "9f4b2c8e1a7d305b");
            vault.UpsertGitHubPrCache(
                prKey,
                "dustinblad/rhyolite",
                142,
                "9f4b2c8e1a7d305b",
                "W/\"etag-142-9f4b2c8\"",
                "# PR #142 Cached Review Analysis");

            var swWal = Stopwatch.StartNew();
            var prCacheHit = vault.TryGetGitHubPrCache(prKey);
            swWal.Stop();

            Assert.True(prCacheHit.Found);
            Assert.Equal("9f4b2c8e1a7d305b", prCacheHit.HeadSha);
            Assert.Equal("W/\"etag-142-9f4b2c8\"", prCacheHit.Etag);
            Assert.Equal("# PR #142 Cached Review Analysis", prCacheHit.AnalysisMarkdown);
            Assert.True(swWal.Elapsed.TotalMilliseconds < 15.0, $"Expected SQLite WAL PR cache lookup < 15 ms, got {swWal.Elapsed.TotalMilliseconds:F3} ms.");

            // Verify SQLite WAL external_sync_bindings O(1) primary key upsert & scalar lookup
            vault.UpsertExternalSyncBinding("Bladbase", "ws-bladbase-core", "01 — Architecture/Storage Architecture.md", "sha256-bb-sync-999");
            vault.UpsertExternalSyncBinding("Docmost", "spc-docmost-arch", "01 — Architecture/Storage Architecture.md", "sha256-dm-sync-888");

            Assert.Equal("sha256-bb-sync-999", vault.GetLastSyncedHash("Bladbase", "ws-bladbase-core", "01 — Architecture/Storage Architecture.md"));
            Assert.Equal("sha256-dm-sync-888", vault.GetLastSyncedHash("Docmost", "spc-docmost-arch", "01 — Architecture/Storage Architecture.md"));

            // 2. Verify MCP JSON-RPC 2.0 DevEx tools (rhyolite.scan_secrets, rhyolite.analyze_github_pr, rhyolite.verify_workspace_connectors)
            var auth = new RhyoliteAuthEngine();
            _ = auth.RegisterAccount("architect@rhyolite.dev", "Architect", "Rhyolite!AppleDesign#2026", VaultRole.VaultOwner);
            var login = auth.LoginWithEmailPassword("architect@rhyolite.dev", "Rhyolite!AppleDesign#2026");
            Assert.NotNull(login);
            var peerSync = new PeerSyncEngine("node-verify");
            var mcp = new McpServerEngine(auth, peerSync, vault);

            Assert.Contains(mcp.Tools, t => t.Name == "rhyolite.scan_secrets");
            Assert.Contains(mcp.Tools, t => t.Name == "rhyolite.analyze_github_pr");
            Assert.Contains(mcp.Tools, t => t.Name == "rhyolite.verify_workspace_connectors");

            string scanRpc = mcp.HandleJsonRpc("""
                {
                  "jsonrpc": "2.0",
                  "id": 401,
                  "method": "tools/call",
                  "params": {
                    "name": "rhyolite.scan_secrets",
                    "arguments": {
                      "path": "Runbooks/Deploy.md",
                      "content": "export GITHUB_TOKEN=ghp_1234567890abcdefghijklmnopqrstuvwx",
                      "backend": "keepassxc"
                    }
                  }
                }
                """, login!.RawBearerToken);
            Assert.Contains("keepassxc://Engineering/GitHub-Enterprise/GITHUB_TOKEN", scanRpc);

            string prRpc = mcp.HandleJsonRpc("""
                {
                  "jsonrpc": "2.0",
                  "id": 402,
                  "method": "tools/call",
                  "params": {
                    "name": "rhyolite.analyze_github_pr",
                    "arguments": {
                      "prNumber": 142,
                      "repository": "dustinblad/rhyolite"
                    }
                  }
                }
                """, login.RawBearerToken);
            Assert.Contains("GetPullRequestSubgraph", prRpc);
            Assert.Contains("pr_cache:dustinblad/rhyolite#142:9f4b2c8e1a7d305b", prRpc);

            string connRpc = mcp.HandleJsonRpc("""
                {
                  "jsonrpc": "2.0",
                  "id": 403,
                  "method": "tools/call",
                  "params": {
                    "name": "rhyolite.verify_workspace_connectors",
                    "arguments": {}
                  }
                }
                """, login.RawBearerToken);
            Assert.Contains("dustinblad/bladbase", connRpc);
            Assert.Contains("Docmost", connRpc);

            // 3. Verify GraphQL fields (devExPrPreview & workspaceConnectors)
            var gql = new GraphQlEngine(auth, peerSync, mcp, vault);
            var gqlRes = gql.ExecuteQuery("""
                query {
                  devExPrPreview(prNumber: 142) {
                    repository
                    prNumber
                    sqliteWalCacheKey
                    riskLevel
                  }
                  workspaceConnectors {
                    connectorId
                    provider
                    workspaceOrSpace
                    status
                  }
                }
                """, login.RawBearerToken);

            Assert.False(gqlRes.HasErrors);
            string gqlJson = gqlRes.Data!.ToJsonString();
            Assert.Contains("pr_cache:dustinblad/rhyolite#142:9f4b2c8e1a7d305b", gqlJson);
            Assert.Contains("dustinblad/bladbase", gqlJson);
            Assert.Contains("Engineering Architecture", gqlJson);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
