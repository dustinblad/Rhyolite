using System.Diagnostics;
using System.Text.Json.Nodes;
using Rhyolite.Core.Bases;
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

            // Verify component geometry, typography stacks, border bevels, and button hex resolvers
            Assert.False(string.IsNullOrWhiteSpace(preset.UiFontFamily));
            Assert.False(string.IsNullOrWhiteSpace(preset.DisplayFontFamily));
            Assert.False(string.IsNullOrWhiteSpace(preset.MonoFontFamily));
            Assert.False(string.IsNullOrWhiteSpace(preset.SkiaUiFontName));
            Assert.False(string.IsNullOrWhiteSpace(preset.SkiaDisplayFontName));
            Assert.False(string.IsNullOrWhiteSpace(preset.SkiaMonoFontName));
            Assert.False(string.IsNullOrWhiteSpace(preset.ComponentStyleSummary));
            Assert.False(string.IsNullOrWhiteSpace(preset.PrimaryButtonBorderThickness));
            Assert.False(string.IsNullOrWhiteSpace(preset.SecondaryButtonBorderThickness));
            Assert.False(string.IsNullOrWhiteSpace(preset.ButtonPadding));
            Assert.False(string.IsNullOrWhiteSpace(preset.CardBorderThickness));
            Assert.False(string.IsNullOrWhiteSpace(preset.CardPadding));

            foreach (bool isDark in new[] { false, true })
            {
                string btnBg = preset.GetPrimaryButtonBgHex(isDark);
                string btnFg = preset.GetPrimaryButtonFgHex(isDark);
                string btnBorder = preset.GetPrimaryButtonBorderHex(isDark);
                Assert.StartsWith("#", btnBg, StringComparison.Ordinal);
                Assert.StartsWith("#", btnFg, StringComparison.Ordinal);
                Assert.StartsWith("#", btnBorder, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(preset.GetPrimaryButtonShadow(isDark)));
                Assert.False(string.IsNullOrWhiteSpace(preset.GetSecondaryButtonShadow(isDark)));
                Assert.False(string.IsNullOrWhiteSpace(preset.GetCardShadow(isDark)));
            }

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

        // Verify distinct per-preset component geometries & typography stacks
        var applePreset = DesignSystemCatalog.GetOrDefault("apple");
        var linearPreset = DesignSystemCatalog.GetOrDefault("linear-app");
        var notionPreset = DesignSystemCatalog.GetOrDefault("notion");
        var vercelPreset = DesignSystemCatalog.GetOrDefault("vercel");
        var claudePreset = DesignSystemCatalog.GetOrDefault("claude");
        var githubPreset = DesignSystemCatalog.GetOrDefault("github");
        var raycastPreset = DesignSystemCatalog.GetOrDefault("raycast");
        var stripePreset = DesignSystemCatalog.GetOrDefault("stripe");
        var supabasePreset = DesignSystemCatalog.GetOrDefault("supabase");

        Assert.Equal(0.0, vercelPreset.PrimaryButtonCornerRadius);
        Assert.Equal(0.0, vercelPreset.CardCornerRadius);
        Assert.Contains("monospace", vercelPreset.UiFontFamily, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("serif", claudePreset.UiFontFamily, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("serif", notionPreset.UiFontFamily, StringComparison.OrdinalIgnoreCase);

        Assert.True(applePreset.PrimaryButtonCornerRadius >= 980.0);
        Assert.True(stripePreset.PrimaryButtonCornerRadius >= 980.0);

        Assert.Equal("1,2,1,1", linearPreset.PrimaryButtonBorderThickness);
        Assert.Equal("1,1,1,2", githubPreset.PrimaryButtonBorderThickness);
        Assert.Equal("#1F883D", githubPreset.GetPrimaryButtonBgHex(isDarkMode: false));
        Assert.Equal("#238636", githubPreset.GetPrimaryButtonBgHex(isDarkMode: true));
        Assert.Equal("1,1,1,3", raycastPreset.PrimaryButtonBorderThickness);
        Assert.Equal("#090D0B", supabasePreset.GetPrimaryButtonFgHex(isDarkMode: true));
        Assert.Equal("#3ECF8E", supabasePreset.GetPrimaryButtonBgHex(isDarkMode: true));

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

    [Fact]
    public async Task FullAppUsability_SnapshotRestore_FlashcardPersistence_NoteDelete_AndLivePrFallback_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteFullUsabilityTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var vault = new HybridVaultEngine(tempDir);
            await vault.LoadOrSeedVaultAsync();

            // 1. Verify SaveFlashcardReview & GetFlashcardReview round-trip in SQLite WAL
            var nextReview = DateTimeOffset.UtcNow.AddDays(7);
            vault.SaveFlashcardReview("fc-usability-card-01", stabilityDays: 14.5, difficulty: 4.2, reps: 3, nextReviewUtc: nextReview);
            var loadedReview = vault.GetFlashcardReview("fc-usability-card-01");
            Assert.NotNull(loadedReview);
            Assert.Equal(14.5, loadedReview!.Value.StabilityDays, 3);
            Assert.Equal(4.2, loadedReview.Value.Difficulty, 3);
            Assert.Equal(3, loadedReview.Value.Reps);
            Assert.True(Math.Abs((loadedReview.Value.NextReviewUtc - nextReview).TotalSeconds) < 2.0);

            // 2. Verify SaveMilestoneSnapshot & RestoreSnapshotAsync restore previous note content on disk
            const string snapNotePath = "01 — Architecture/Snapshot TimeMachine Spec.md";
            const string v1Content = "# Snapshot TimeMachine Spec\nVersion 1: Canonical baseline architecture.";
            await vault.SaveNoteAsync(snapNotePath, v1Content, "Baseline v1");
            var milestoneSnap = vault.SaveMilestoneSnapshot(snapNotePath, "Milestone v1 Golden");
            Assert.Equal("Milestone v1 Golden", milestoneSnap.Label);

            const string v2Content = "# Snapshot TimeMachine Spec\nVersion 2: Experimental draft mutation.";
            await vault.SaveNoteAsync(snapNotePath, v2Content, "Draft v2");
            string diskV2 = await File.ReadAllTextAsync(Path.Combine(tempDir, snapNotePath));
            Assert.Contains("Version 2: Experimental draft mutation.", diskV2);

            var restoredNote = await vault.RestoreSnapshotAsync(
                snapNotePath,
                new DateTimeOffset(DateTime.SpecifyKind(milestoneSnap.CreatedUtc, DateTimeKind.Utc)));
            Assert.NotNull(restoredNote);
            Assert.Contains("Version 1: Canonical baseline architecture.", restoredNote!.RawMarkdown);
            string diskRestored = await File.ReadAllTextAsync(Path.Combine(tempDir, snapNotePath));
            Assert.Contains("Version 1: Canonical baseline architecture.", diskRestored);

            // 3. Verify DeleteNoteAsync deletes a note from disk and SQLite WAL
            const string deleteNotePath = "02 — Daily Notes/Ephemeral Scratchpad.md";
            var createdToDelete = await vault.SaveNoteAsync(deleteNotePath, "# Ephemeral Scratchpad\nTo be deleted.", "Create Scratch");
            Assert.True(File.Exists(createdToDelete.FullPath));
            bool deleted = await vault.DeleteNoteAsync(deleteNotePath);
            Assert.True(deleted);
            Assert.False(File.Exists(createdToDelete.FullPath));
            Assert.DoesNotContain(vault.Notes, n => string.Equals(n.RelativePath, deleteNotePath, StringComparison.OrdinalIgnoreCase));

            // 4. Verify SyncWithBladbaseWorkspaceAsync persists external_sync_bindings in SQLite WAL
            var connectors = new WorkspaceConnectorsEngine();
            var bbConfig = WorkspaceConnectorsEngine.CreateDefaultBladbaseConfig("ws-bladbase-live-test");
            var syncSummary = await connectors.SyncWithBladbaseWorkspaceAsync(vault, bbConfig);
            Assert.True(syncSummary.PulledCount + syncSummary.PushedCount + syncSummary.MergedCount > 0);

            var persistedBindings = vault.GetExternalSyncBindings("Bladbase");
            Assert.NotEmpty(persistedBindings);
            Assert.Contains(persistedBindings, b => b.WorkspaceOrSpaceId == "ws-bladbase-live-test" && !string.IsNullOrWhiteSpace(b.LastSyncedSha256));

            // 5. Verify FetchAndAnalyzePullRequestLiveOrFallbackAsync populates github_pr_cache and analyzes PR blast radius
            var prReport = await EnterpriseDevExEngine.FetchAndAnalyzePullRequestLiveOrFallbackAsync(
                "dustinblad/rhyolite",
                142,
                githubToken: null,
                vault);
            Assert.Equal("dustinblad/rhyolite", prReport.Repository);
            Assert.Equal(142, prReport.PrNumber);
            Assert.True(prReport.TotalAdditions > 0);
            Assert.Contains("PR #142", prReport.GeneratedReviewMarkdown);

            var walPrHit = vault.TryGetGitHubPrCache(prReport.SqliteWalCacheKey);
            Assert.True(walPrHit.Found);
            Assert.Contains("PR #142", walPrHit.AnalysisMarkdown);

            // 6. Verify ExecuteRunbookCellAsync(..., allowDestructiveOverride: true) allows confirmed execution of guarded runbook cells
            const string destructiveRunbookMd = """
                # Production Emergency Remediation
                ```bash env=prod
                kubectl delete pod -l app=rhyolite-sync --force
                ```
                """;
            var cells = EnterpriseDevExEngine.ExtractRunbookCells(destructiveRunbookMd);
            Assert.Single(cells);
            var guardedCell = cells[0];
            Assert.True(guardedCell.RequiresConfirmation);
            Assert.False(guardedCell.IsReadOnlySafe);

            var blockedResult = await EnterpriseDevExEngine.ExecuteRunbookCellAsync(guardedCell, allowDestructiveOverride: false);
            Assert.False(blockedResult.Executed);
            Assert.True(blockedResult.BlockedByGuardrail);
            Assert.Contains("[GUARDRAIL BLOCKED]", blockedResult.Output);

            var overriddenResult = await EnterpriseDevExEngine.ExecuteRunbookCellAsync(guardedCell, allowDestructiveOverride: true);
            Assert.True(overriddenResult.Executed);
            Assert.False(overriddenResult.BlockedByGuardrail);
            Assert.Contains("[GUARDRAIL OVERRIDDEN BY USER]", overriddenResult.Output);
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
    public async Task ObsidianFolderSubfolderHierarchy_EmojiAssignment_AndCapacitiesObjectStudio_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteFolderCapacitiesTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var vault = new HybridVaultEngine(tempDir);
            await vault.LoadOrSeedVaultAsync();

            // 1. Create nested Obsidian folders & subfolders and assign custom icons/emojis & accent colors in SQLite WAL
            const string nestedFolderPath = "01 — Architecture/Storage/Replication";
            var createdSubfolder = vault.CreateFolderOrSubfolder(nestedFolderPath, iconEmoji: "💎", colorHex: "#8B5CF6");
            Assert.Equal(nestedFolderPath, createdSubfolder.FolderPath);
            Assert.Equal("Replication", createdSubfolder.DisplayName);
            Assert.Equal(2, createdSubfolder.Depth);
            Assert.Equal("💎", createdSubfolder.IconEmoji);
            Assert.True(Directory.Exists(Path.Combine(tempDir, "01 — Architecture", "Storage", "Replication")));

            vault.SetFolderIconAndColor("01 — Architecture/Storage", "🔥", "#F97316");
            var storageMeta = vault.GetFolderMetadata("01 — Architecture/Storage");
            Assert.Equal("🔥", storageMeta.IconEmoji);
            Assert.Equal("#F97316", storageMeta.ColorHex);
            Assert.True(storageMeta.IsExpanded);

            var replicationMeta = vault.GetFolderMetadata(nestedFolderPath);
            Assert.Equal("💎", replicationMeta.IconEmoji);
            Assert.Equal("#8B5CF6", replicationMeta.ColorHex);

            // Verify hierarchical tree and expand/collapse toggle in SQLite WAL
            var rootTree = vault.GetFolderHierarchyTree();
            Assert.NotEmpty(rootTree);
            var archRoot = rootTree.First(r => r.FolderPath == "01 — Architecture");
            var storageChild = archRoot.Subfolders.First(s => s.FolderPath == "01 — Architecture/Storage");
            Assert.Equal("🔥", storageChild.IconEmoji);
            var replicationLeaf = storageChild.Subfolders.First(s => s.FolderPath == nestedFolderPath);
            Assert.Equal("💎", replicationLeaf.IconEmoji);

            var visibleBeforeCollapse = vault.GetFlattenedVisibleFolderTree();
            Assert.Contains(visibleBeforeCollapse, f => f.FolderPath == nestedFolderPath);

            bool collapsedState = vault.ToggleFolderExpanded("01 — Architecture/Storage");
            Assert.False(collapsedState);
            var visibleAfterCollapse = vault.GetFlattenedVisibleFolderTree();
            Assert.DoesNotContain(visibleAfterCollapse, f => f.FolderPath == nestedFolderPath);

            bool reExpandedState = vault.ToggleFolderExpanded("01 — Architecture/Storage");
            Assert.True(reExpandedState);

            // 2. Move a note into the nested subfolder via MoveNoteToFolderAsync and verify FolderPath & FolderIconEmoji
            var movedNote = await vault.MoveNoteToFolderAsync("01 — Architecture/Storage Architecture.md", nestedFolderPath);
            Assert.NotNull(movedNote);
            Assert.Equal(nestedFolderPath, movedNote!.FolderPath);
            Assert.Equal("💎", movedNote.FolderIconEmoji);
            Assert.Equal($"{nestedFolderPath}/Storage Architecture.md", movedNote.RelativePath);
            Assert.True(File.Exists(movedNote.FullPath));

            // 3. Create a custom Capacities Object Type in Object Studio and persist in SQLite WAL
            var customRfcSchema = new CapacitiesObjectTypeSchema(
                TypeId: "engineering-rfc",
                SingularName: "Engineering RFC",
                PluralName: "Engineering RFCs",
                IconEmoji: "📐",
                AccentColorHex: "#EC4899",
                DefaultFolderPath: nestedFolderPath,
                SchemaPropertyKeys: ["type", "status", "author", "target_release", "collection"],
                Description: "Capacities Object Studio schema for distributed systems RFCs");
            vault.SaveCustomObjectType(customRfcSchema);

            var schemas = vault.GetObjectTypeSchemas();
            Assert.Contains(schemas, s => s.TypeId == "engineering-rfc" && s.IconEmoji == "📐" && s.SingularName == "Engineering RFC");

            // Create a typed Object note using CreateTypedObjectNoteAsync
            var createdRfcNote = await vault.CreateTypedObjectNoteAsync(
                "Engineering RFC",
                "RFC-042 Multi-Region HLC Replication",
                targetFolder: nestedFolderPath,
                collectionName: "Distributed Storage");
            Assert.Equal("Engineering RFC", createdRfcNote.ObjectType);
            Assert.Equal("📐", createdRfcNote.ObjectIconEmoji);
            Assert.Equal("💎", createdRfcNote.FolderIconEmoji);
            Assert.Equal("Distributed Storage", createdRfcNote.CollectionName);
            Assert.Equal(nestedFolderPath, createdRfcNote.FolderPath);

            // 4. Query BasesQueryEngine.QueryRows, GetCollections, and BuildCalendarTimeline
            var rfcRows = BasesQueryEngine.QueryRows(
                vault.Notes,
                folderFilter: "01 — Architecture/Storage",
                objectTypeFilter: "Engineering RFC",
                collectionFilter: "Distributed Storage");
            Assert.Single(rfcRows);
            Assert.Equal("RFC-042 Multi-Region HLC Replication", rfcRows[0].Title);
            Assert.Equal("📐", rfcRows[0].ObjectIconEmoji);
            Assert.Equal("💎", rfcRows[0].FolderIconEmoji);
            Assert.Equal("Distributed Storage", rfcRows[0].CollectionName);

            var collections = BasesQueryEngine.GetCollections(vault.Notes);
            Assert.Contains(collections, c => c.Name == "Distributed Storage" && c.ItemCount >= 1);

            var timeline = BasesQueryEngine.BuildCalendarTimeline(vault.Notes);
            Assert.NotEmpty(timeline);
            Assert.Contains(timeline, e =>
                e.Title == "RFC-042 Multi-Region HLC Replication" &&
                e.ObjectType == "Engineering RFC" &&
                e.ObjectIconEmoji == "📐" &&
                e.FolderIconEmoji == "💎");

            // 5. Verify Unified Hierarchical Folder & File Explorer Tree (DESIGN.md Pre-Order Specification)
            var movedStorageNote = await vault.MoveNoteToFolderAsync(
                "01 — Architecture/Avalonia Rendering Pipeline.md",
                "01 — Architecture/Storage");
            Assert.NotNull(movedStorageNote);
            Assert.Equal("01 — Architecture/Storage/Avalonia Rendering Pipeline.md", movedStorageNote!.RelativePath);

            var unifiedTree = vault.GetUnifiedExplorerTree(
                selectedNotePath: createdRfcNote.RelativePath,
                selectedFolderPath: "01 — Architecture/Storage");
            Assert.NotEmpty(unifiedTree);

            int idxArchRoot = unifiedTree.ToList().FindIndex(i => i.IsFolder && i.RelativePath == "01 — Architecture");
            int idxStorageSubfolder = unifiedTree.ToList().FindIndex(i => i.IsFolder && i.RelativePath == "01 — Architecture/Storage");
            int idxReplicationSubfolder = unifiedTree.ToList().FindIndex(i => i.IsFolder && i.RelativePath == nestedFolderPath);
            int idxDeepNote = unifiedTree.ToList().FindIndex(i => i.IsNote && i.RelativePath == createdRfcNote.RelativePath);
            int idxNestedStorageNote = unifiedTree.ToList().FindIndex(i => i.IsNote && i.RelativePath == movedStorageNote.RelativePath);
            int idxParentArchNote = unifiedTree.ToList().FindIndex(i => i.IsNote && i.RelativePath == "01 — Architecture/Apple Design Tokens.md");

            Assert.True(idxArchRoot >= 0);
            Assert.True(idxStorageSubfolder > idxArchRoot);
            Assert.True(idxReplicationSubfolder > idxStorageSubfolder);
            Assert.True(idxDeepNote > idxReplicationSubfolder);
            Assert.True(idxNestedStorageNote > idxDeepNote);
            Assert.True(idxParentArchNote > idxNestedStorageNote);

            Assert.Equal(0, unifiedTree[idxArchRoot].Depth);
            Assert.Equal(1, unifiedTree[idxStorageSubfolder].Depth);
            Assert.True(unifiedTree[idxStorageSubfolder].IsSelected);
            Assert.Equal(2, unifiedTree[idxReplicationSubfolder].Depth);
            Assert.Equal(3, unifiedTree[idxDeepNote].Depth);
            Assert.True(unifiedTree[idxDeepNote].IsSelected);
            Assert.Equal("📐", unifiedTree[idxDeepNote].IconEmoji);
            Assert.Equal(2, unifiedTree[idxNestedStorageNote].Depth);
            Assert.False(unifiedTree[idxNestedStorageNote].IsSelected);
            Assert.Equal(1, unifiedTree[idxParentArchNote].Depth);

            // Collapsing "01 — Architecture/Storage" hides both its child subfolders and child notes from GetUnifiedExplorerTree
            Assert.False(vault.ToggleFolderExpanded("01 — Architecture/Storage"));
            var collapsedTree = vault.GetUnifiedExplorerTree(selectedNotePath: createdRfcNote.RelativePath);
            var collapsedStorageFolder = Assert.Single(collapsedTree, i => i.IsFolder && i.RelativePath == "01 — Architecture/Storage");
            Assert.False(collapsedStorageFolder.IsExpanded);
            Assert.Equal("▸", collapsedStorageFolder.ChevronGlyph);
            Assert.DoesNotContain(collapsedTree, i => i.RelativePath == nestedFolderPath);
            Assert.DoesNotContain(collapsedTree, i => i.RelativePath == createdRfcNote.RelativePath);
            Assert.DoesNotContain(collapsedTree, i => i.RelativePath == movedStorageNote.RelativePath);
            Assert.Contains(collapsedTree, i => i.IsNote && i.RelativePath == "01 — Architecture/Apple Design Tokens.md");

            // Re-expanding restores the nested subfolder and child notes
            Assert.True(vault.ToggleFolderExpanded("01 — Architecture/Storage"));
            var restoredTree = vault.GetUnifiedExplorerTree(selectedNotePath: createdRfcNote.RelativePath);
            Assert.Contains(restoredTree, i => i.IsFolder && i.RelativePath == nestedFolderPath);
            Assert.Contains(restoredTree, i => i.IsNote && i.RelativePath == createdRfcNote.RelativePath && i.IsSelected);
            Assert.Contains(restoredTree, i => i.IsNote && i.RelativePath == movedStorageNote.RelativePath);
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
    public async Task UserAccountProfile_AvatarImagePersistence_AndEnterpriseAdminRbac_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteAccountAdminTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // 1. Verify default owner UserProfileRecord on initial vault seed
            using (var vault = new HybridVaultEngine(tempDir))
            {
                await vault.LoadOrSeedVaultAsync();

                var defaultProfile = vault.GetUserProfile();
                Assert.Equal("Dustin Blad", defaultProfile.DisplayName);
                Assert.Equal("dustin@rhyolite.dev", defaultProfile.Email);
                Assert.Equal("VaultOwner · Admin", defaultProfile.RoleBadge);
                Assert.Equal("DB", defaultProfile.AvatarInitialsOrEmoji);
                Assert.Equal("Initials", defaultProfile.AvatarMode);

                // 2. Persist updated UserProfileRecord fields in SQLite WAL ui_preferences
                var updatedProfile = vault.SaveUserProfile(defaultProfile with
                {
                    DisplayName = "Dustin Blad (Staff Architect)",
                    Email = "dustin.blad@rhyolite.enterprise",
                    WorkspaceSubtitle = "Core Platform Engineering",
                    JobTitle = "Distinguished Systems Engineer",
                    Organization = "Rhyolite Enterprise Platform",
                    RoleBadge = "VaultOwner · Global Admin",
                    PresenceStatus = "Focusing",
                    AvatarInitialsOrEmoji = "⚡",
                    AvatarAccentHex = "#8B5CF6"
                });
                Assert.Equal("Dustin Blad (Staff Architect)", updatedProfile.DisplayName);

                var reloadedProfile = vault.GetUserProfile();
                Assert.Equal("Dustin Blad (Staff Architect)", reloadedProfile.DisplayName);
                Assert.Equal("dustin.blad@rhyolite.enterprise", reloadedProfile.Email);
                Assert.Equal("Core Platform Engineering", reloadedProfile.WorkspaceSubtitle);
                Assert.Equal("Distinguished Systems Engineer", reloadedProfile.JobTitle);
                Assert.Equal("Rhyolite Enterprise Platform", reloadedProfile.Organization);
                Assert.Equal("VaultOwner · Global Admin", reloadedProfile.RoleBadge);
                Assert.Equal("Focusing", reloadedProfile.PresenceStatus);
                Assert.Equal("⚡", reloadedProfile.AvatarInitialsOrEmoji);
                Assert.Equal("#8B5CF6", reloadedProfile.AvatarAccentHex);

                // 3. Save custom avatar image bytes atomically to .rhyolite/avatar.png
                byte[] samplePngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
                string savedAvatarPath = vault.SaveProfileAvatarBytes(samplePngBytes, ".png");
                Assert.True(File.Exists(savedAvatarPath));
                Assert.Equal(samplePngBytes, await File.ReadAllBytesAsync(savedAvatarPath));

                var profileWithBytesAvatar = vault.GetUserProfile();
                Assert.Equal("Image", profileWithBytesAvatar.AvatarMode);
                Assert.Equal(savedAvatarPath, profileWithBytesAvatar.AvatarImagePath);

                // 4. Copy an external profile image file into .rhyolite/avatar.png via CopyProfileAvatarFromFile
                string externalImagePath = Path.Combine(tempDir, "external-upload-avatar.png");
                byte[] externalPngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04, 0x05];
                await File.WriteAllBytesAsync(externalImagePath, externalPngBytes);

                string? copiedAvatarPath = vault.CopyProfileAvatarFromFile(externalImagePath);
                Assert.NotNull(copiedAvatarPath);
                Assert.True(File.Exists(copiedAvatarPath!));
                Assert.Equal(externalPngBytes, await File.ReadAllBytesAsync(copiedAvatarPath!));
            }

            // 5. Cold restart verification: ensure UserProfileRecord & avatar image survive a fresh HybridVaultEngine instance
            using (var restartedVault = new HybridVaultEngine(tempDir))
            {
                await restartedVault.LoadOrSeedVaultAsync();
                var persistedProfile = restartedVault.GetUserProfile();
                Assert.Equal("Dustin Blad (Staff Architect)", persistedProfile.DisplayName);
                Assert.Equal("dustin.blad@rhyolite.enterprise", persistedProfile.Email);
                Assert.Equal("Distinguished Systems Engineer", persistedProfile.JobTitle);
                Assert.Equal("Rhyolite Enterprise Platform", persistedProfile.Organization);
                Assert.Equal("Image", persistedProfile.AvatarMode);
                Assert.NotNull(persistedProfile.AvatarImagePath);
                Assert.True(File.Exists(persistedProfile.AvatarImagePath!));
            }

            // 6. Enterprise Admin IAM / RBAC user provisioning, OAuth2/OIDC SSO, API keys & audit log verification
            var auth = new RhyoliteAuthEngine();
            var ownerAccount = auth.RegisterAccount(
                "dustin.blad@rhyolite.enterprise",
                "Dustin Blad",
                "OwnerEnterprisePass#2026!",
                VaultRole.VaultOwner,
                iterations: 10_000);
            var editorAccount = auth.RegisterAccount(
                "staff.editor@rhyolite.enterprise",
                "Elena Rostova",
                "EditorEnterprisePass#2026!",
                VaultRole.Editor,
                iterations: 10_000);
            var agentAccount = auth.RegisterAccount(
                "claude-agent@rhyolite.enterprise",
                "Claude Code MCP Harness",
                "AgentEnterprisePass#2026!",
                VaultRole.AiAgentReadWrite,
                AccessScope.ForAiAgent(allowedFolders: ["01 — Architecture", "04 — Capacities Studio"]),
                iterations: 10_000);
            var peerAccount = auth.RegisterAccount(
                "mac-studio-peer@rhyolite.enterprise",
                "Mac Studio M4 Max Peer Node",
                "PeerEnterprisePass#2026!",
                VaultRole.TrustedPeerNode,
                iterations: 10_000);
            var readerAccount = auth.RegisterAccount(
                "auditor@rhyolite.enterprise",
                "SOC2 Security Auditor",
                "AuditorEnterprisePass#2026!",
                VaultRole.Reader,
                iterations: 10_000);

            Assert.Equal(5, auth.Accounts.Count);
            Assert.Contains(auth.Accounts, a => a.Role == VaultRole.VaultOwner && a.Email == ownerAccount.Email);
            Assert.Contains(auth.Accounts, a => a.Role == VaultRole.Editor && a.Email == editorAccount.Email);
            Assert.Contains(auth.Accounts, a => a.Role == VaultRole.AiAgentReadWrite && a.Email == agentAccount.Email);
            Assert.Contains(auth.Accounts, a => a.Role == VaultRole.TrustedPeerNode && a.Email == peerAccount.Email);
            Assert.Contains(auth.Accounts, a => a.Role == VaultRole.Reader && a.Email == readerAccount.Email);

            // Verify OAuth2 / OIDC SSO provider registry and PKCE S256 round-trip
            var providers = auth.GetIdentityProviders();
            Assert.Equal(6, providers.Count);
            var oidcFlow = auth.BeginOAuthFlow(
                OAuthProviderType.EnterpriseSsoOidc,
                clientId: "rhyolite-enterprise-admin",
                redirectUri: "rhyolite://auth/oidc/callback",
                domainHint: "rhyolite.enterprise");
            Assert.Contains("code_challenge_method=S256", oidcFlow.AuthorizationUrl);

            var oidcToken = auth.CompleteOAuthFlow(
                OAuthProviderType.EnterpriseSsoOidc,
                oidcFlow.Pkce.State,
                oidcFlow.Pkce.CodeVerifier,
                "sso.admin@rhyolite.enterprise",
                "SSO Admin Principal",
                VaultRole.Admin);
            Assert.Equal(VaultRole.Admin, oidcToken.Role);

            // Verify API key issuance & revocation
            var issuedKey = auth.CreateApiKey(
                ownerAccount.UserId,
                "Production MCP Agent Token",
                VaultRole.AiAgentReadWrite);
            Assert.StartsWith("rhy_pat_", issuedKey.RawApiKey);
            Assert.True(auth.TryValidateToken(issuedKey.RawApiKey, out var validatedRole, out _, out _, out _));
            Assert.Equal(VaultRole.AiAgentReadWrite, validatedRole);

            Assert.True(auth.RevokeApiKey(issuedKey.Credential.KeyId));
            Assert.False(auth.TryValidateToken(issuedKey.RawApiKey, out _, out _, out _, out var revokeReason));
            Assert.Contains("revoked", revokeReason, StringComparison.OrdinalIgnoreCase);

            // Verify AdminAuditLogEntry record construction
            var auditEntry = new AdminAuditLogEntry(
                TimestampDisplay: "2026-11-05 20:38 UTC",
                ActorEmail: ownerAccount.Email,
                ActionTitle: "Revoked API Key 'Production MCP Agent Token'",
                TargetResource: issuedKey.Credential.KeyId,
                SeverityBadge: "SECURITY");
            Assert.Equal("SECURITY", auditEntry.SeverityBadge);
            Assert.Equal(ownerAccount.Email, auditEntry.ActorEmail);
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
