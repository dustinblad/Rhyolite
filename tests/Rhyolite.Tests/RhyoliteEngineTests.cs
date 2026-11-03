using Rhyolite.Core.Bases;
using Rhyolite.Core.Canvas;
using Rhyolite.Core.Enterprise;
using Rhyolite.Core.Expansions;
using Rhyolite.Core.Graph;
using Rhyolite.Core.Models;
using Rhyolite.Core.Parsing;
using Rhyolite.Core.Protocol;
using Rhyolite.Core.Storage;
using Xunit;

namespace Rhyolite.Tests;

public sealed class RhyoliteEngineTests
{
    [Fact]
    public async Task HybridVaultEngine_SeedsStarterVault_WritesMarkdownFast_AndRefactorsWikiLinksOnRename()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteTestVault_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var engine = new HybridVaultEngine(tempDir);
            await engine.LoadOrSeedVaultAsync();

            Assert.True(engine.Notes.Count >= 7);
            Assert.True(File.Exists(Path.Combine(tempDir, ".rhyolite", "cache.db")));

            // Fast update of an .md file
            var storageNote = engine.Notes.First(n => n.Title == "Storage Architecture");
            string updatedMd = storageNote.RawMarkdown + "\n- [ ] Verify sub-millisecond flush 📅 2026-11-04 #perf\n";
            var saved = await engine.SaveNoteAsync(storageNote.RelativePath, updatedMd, "Unit Test Update");

            Assert.Contains("Verify sub-millisecond flush", File.ReadAllText(saved.FullPath));
            Assert.True(saved.Tasks.Count >= 4);
            Assert.True(engine.GetSnapshotsForNote(saved.RelativePath).Count >= 2);

            // Rename "Storage Architecture" -> "Hybrid Core Storage" and verify automatic WikiLink refactoring
            var renamed = await engine.RenameNoteAndRefactorLinksAsync(saved.RelativePath, "Hybrid Core Storage");
            Assert.NotNull(renamed);
            Assert.Equal("Hybrid Core Storage", renamed!.Title);

            var avaloniaNote = engine.Notes.First(n => n.Title == "Avalonia Rendering Pipeline");
            Assert.Contains("[[Hybrid Core Storage]]", avaloniaNote.RawMarkdown);
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
    public void MarkdownAstEngine_ParsesFrontmatter_WikiLinks_Callouts_Tasks_AndFlashcards()
    {
        var note = new VaultNote
        {
            Id = "test.md",
            RelativePath = "01 — Architecture/test.md",
            FullPath = "/tmp/test.md",
            Title = "Test Note",
            FolderPath = "01 — Architecture",
            RawMarkdown = """
                ---
                status: Shipped
                priority: P0 Critical
                tags: [dotnet10, avalonia]
                ---
                # Heading One
                Linking to [[Storage Architecture|Storage Spec]] and embedding ![[System Diagram]].
                Inline tag #apple-design.

                > [!IMPORTANT]+ Key Architecture Decision
                > Plain .md files remain the canonical source of truth.

                - [ ] Ship Windows, macOS, Linux, iOS, and Android builds ⏫ 📅 2026-11-05 #release

                Q: What is the Apple primary action accent color?
                A: #0071E3 (Apple Action Blue)

                Cloze test: {{c1::SQLite WAL}} accelerates cold startup.
                """
        };

        MarkdownAstEngine.ParseNoteMetadata(note);

        Assert.Equal("Shipped", note.GetProperty("status"));
        Assert.Contains("#dotnet10", note.Tags);
        Assert.Contains("#apple-design", note.Tags);
        Assert.Equal(2, note.OutgoingLinks.Count);
        Assert.Single(note.Callouts);
        Assert.Equal("IMPORTANT", note.Callouts[0].CalloutType);
        Assert.Single(note.Tasks);
        Assert.Equal(TaskPriority.High, note.Tasks[0].Priority);
        Assert.Equal("2026-11-05", note.Tasks[0].DueDate);
        Assert.Equal(2, note.Flashcards.Count);
    }

    [Fact]
    public void KnowledgeGraph_Bases_Canvas_AndExpansions_WorkEndToEnd()
    {
        var n1 = new VaultNote
        {
            Id = "A.md",
            RelativePath = "A.md",
            FullPath = "/tmp/A.md",
            Title = "Architecture Hub",
            FolderPath = "Core",
            RawMarkdown = "---\nstatus: Shipped\n---\n# Hub\nLinks to [[Node B]] and discusses hybrid SQLite WAL storage."
        };
        var n2 = new VaultNote
        {
            Id = "B.md",
            RelativePath = "B.md",
            FullPath = "/tmp/B.md",
            Title = "Node B",
            FolderPath = "Core",
            RawMarkdown = "---\nstatus: In Progress\n---\n# Node B\nMentions Architecture Hub without brackets and shares hybrid SQLite WAL storage terms."
        };

        MarkdownAstEngine.ParseNoteMetadata(n1);
        MarkdownAstEngine.ParseNoteMetadata(n2);

        var notes = new[] { n1, n2 };
        var metrics = KnowledgeGraphEngine.RebuildGraphRelationships(notes);

        Assert.Equal(2, metrics.TotalNotes);
        Assert.Single(n2.Backlinks);
        Assert.Single(n1.UnlinkedMentions);

        var (nodes2d, edges2d) = KnowledgeGraphEngine.CreateForceLayout(notes);
        Assert.Equal(2, nodes2d.Count);
        Assert.Single(edges2d);

        // Bases Table & Kanban
        var rows = BasesQueryEngine.QueryRows(notes);
        Assert.Equal(2, rows.Count);
        var kanban = BasesQueryEngine.BuildKanbanBoard(notes);
        Assert.Equal(3, kanban.Count);

        // JSON Canvas 1.0 roundtrip
        var canvas = JsonCanvasEngine.CreateDefaultArchitectureCanvas();
        var serialized = JsonCanvasEngine.Serialize(canvas);
        var parsed = JsonCanvasEngine.Parse(serialized);
        Assert.Equal(canvas.Nodes.Count, parsed.Nodes.Count);

        // FSRS v4 Spaced Repetition
        var card = new FlashcardItem
        {
            Id = "c1",
            NotePath = "A.md",
            NoteTitle = "Architecture Hub",
            Question = "Q",
            Answer = "A"
        };
        FsrsStudyEngine.ApplyReview(card, FsrsRating.Good);
        Assert.Equal(1, card.Reps);
        Assert.True(card.StabilityDays >= 4.0);

        // Local Vault RAG Copilot
        var rag = VaultAiCopilotEngine.AskVault("SQLite WAL storage", n1, notes);
        Assert.NotEmpty(rag.CitedNoteTitles);
        Assert.NotEmpty(rag.SemanticMatches);

        // Snapshot LCS Diff & Apple HTML Export
        var diff = SnapshotDiffEngine.ComputeLineDiff("Line 1\nOld Line", "Line 1\nNew Line");
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Added);
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Removed);

        string html = AppleHtmlExporter.ExportNoteToAppleHtml(n1, darkMode: true);
        Assert.Contains("#000000", html);
    }

    [Fact]
    public void ProtocolEngine_Auth_Rbac_OAuth_Mcp_GraphQl_AndP2pMesh_WorkEndToEnd()
    {
        var auth = new RhyoliteAuthEngine();
        var peerEngine = new PeerSyncEngine("node-alpha");
        var mcp = new McpServerEngine(auth, peerEngine);
        var gql = new GraphQlEngine(auth, peerEngine, mcp);

        // 1. Register Owner + Scoped AI Agent API Key
        var owner = auth.RegisterAccount("owner@rhyolite.dev", "Owner", "StrongPass!2026", VaultRole.VaultOwner);
        var loggedInToken = auth.LoginWithEmailPassword("owner@rhyolite.dev", "StrongPass!2026");
        Assert.NotNull(loggedInToken);

        var agentScope = AccessScope.ForAiAgent(
            allowedFolders: ["01 — Architecture/"],
            deniedTags: ["#secret"]);
        var apiKey = auth.CreateApiKey(owner.UserId, "Antigravity Harness", VaultRole.AiAgentReadWrite, agentScope);

        // Verify RBAC folder/tag ACLs
        var allowedDecision = auth.CheckAccess(apiKey.RawApiKey, VaultOperation.WriteNote, "01 — Architecture/Spec.md", ["#architecture"]);
        Assert.True(allowedDecision.IsAllowed);

        var deniedFolderDecision = auth.CheckAccess(apiKey.RawApiKey, VaultOperation.WriteNote, "99 — Private/Diary.md", []);
        Assert.False(deniedFolderDecision.IsAllowed);

        var deniedTagDecision = auth.CheckAccess(apiKey.RawApiKey, VaultOperation.ReadNote, "01 — Architecture/Secret.md", ["#secret"]);
        Assert.False(deniedTagDecision.IsAllowed);

        // 2. Verify Pluggable OAuth2/OIDC SSO Providers (Enterprise SSO, Google, GitHub, Microsoft, Apple)
        var providers = auth.GetIdentityProviders();
        Assert.Contains(providers, p => p.ProviderType == OAuthProviderType.EnterpriseSsoOidc);
        Assert.Contains(providers, p => p.ProviderType == OAuthProviderType.Google);
        Assert.Contains(providers, p => p.ProviderType == OAuthProviderType.GitHub);
        Assert.Contains(providers, p => p.ProviderType == OAuthProviderType.Microsoft);
        Assert.Contains(providers, p => p.ProviderType == OAuthProviderType.Apple);

        var oauthStart = auth.BeginOAuthFlow(OAuthProviderType.Apple, "com.rhyolite.app", "https://localhost:7420/auth/callback");
        Assert.Contains("code_challenge=", oauthStart.AuthorizationUrl);

        // 3. Verify High-Speed MCP JSON-RPC 2.0 Write & Read
        string writeRpc = """
            {
              "jsonrpc": "2.0",
              "id": 10,
              "method": "tools/call",
              "params": {
                "name": "rhyolite.write_note",
                "arguments": {
                  "path": "01 — Architecture/Spec.md",
                  "content": "---\nstatus: Active\n---\n# Spec\nLinks to [[Storage Architecture]]."
                }
              }
            }
            """;
        string writeResp = mcp.HandleJsonRpc(writeRpc, apiKey.RawApiKey);
        Assert.Contains("executionMicroseconds", writeResp);

        // 4. Verify GraphQL Query & Mutation
        var gqlResp = gql.Execute(
            new GraphQlRequest("query { notes { path title } authProviders { providerType displayName } }"),
            loggedInToken.RawBearerToken);
        Assert.False(gqlResp.HasErrors);
        Assert.NotNull(gqlResp.Data);

        // 5. Verify Peer-to-Peer Merkle Root & Sync Plan
        var peerBeta = new PeerSyncEngine("node-beta");
        peerBeta.UpsertNote("01 — Architecture/PeerNote.md", "# Peer Note from Beta");

        string rootAlpha = peerEngine.ComputeVaultMerkleRoot();
        string rootBeta = peerBeta.ComputeVaultMerkleRoot();
        Assert.NotEqual(rootAlpha, rootBeta);

        var syncPlan = peerEngine.ComputeSyncPlan("node-beta", peerBeta.GetAllNotes());
        Assert.Contains(syncPlan.Deltas, d => d.Action == SyncActionType.PushToPeer);
        Assert.Contains(syncPlan.Deltas, d => d.Action == SyncActionType.PullFromPeer);
    }

    [Fact]
    public async Task WorkspaceConnectorsEngine_SyncsBladbaseWorkspace_AndDocmostSpace_Bidirectionally()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RhyoliteConnectorsTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var vault = new HybridVaultEngine(tempDir);
            await vault.LoadOrSeedVaultAsync();

            var connectors = new WorkspaceConnectorsEngine();

            // 1. Sync with Bladbase Workspace (Dual Authorship: AI Agent + Human)
            var bladbaseRemote = new[]
            {
                new BladbasePageDocument(
                    PageId: "bb-page-001",
                    WorkspaceId: "ws-bladbase-core",
                    ParentPageId: null,
                    Title: "Bladbase Dual Authorship Protocol",
                    MarkdownBody: "# Bladbase Sync\nCo-authored by humans (TipTap/Yjs) and AI Agents via MCP. Links to [[Storage Architecture]].",
                    AuthorKind: WorkspaceAuthorKind.AiAgent,
                    AuthorId: "antigravity-coding-agent",
                    WorkspaceRole: "owner",
                    ContentSha256: "abc123",
                    UpdatedAtUtc: DateTimeOffset.UtcNow)
            };

            var bbResult = await connectors.SyncBladbaseWorkspaceAsync(vault, "ws-bladbase-core", bladbaseRemote);
            Assert.Equal(ExternalConnectorKind.BladbaseWorkspace, bbResult.ConnectorKind);
            Assert.Equal(1, bbResult.PulledCount);
            Assert.True(bbResult.PushedCount >= 1);

            var syncedBbNote = vault.Notes.FirstOrDefault(n => n.Title == "Bladbase Dual Authorship Protocol");
            Assert.NotNull(syncedBbNote);
            Assert.Equal("agent", syncedBbNote!.GetProperty("bladbase_author_type"));
            Assert.Equal("antigravity-coding-agent", syncedBbNote.GetProperty("bladbase_author_id"));

            // 2. Sync with Docmost Space (ProseMirror/Markdown link normalization)
            var docmostRemote = new[]
            {
                new DocmostPageDocument(
                    PageId: "dm-page-101",
                    SpaceId: "spc-docmost-arch",
                    SpaceSlug: "engineering",
                    ParentPageId: null,
                    Slug: "docmost-space-handbook",
                    Title: "Docmost Space Handbook",
                    MarkdownContent: "# Docmost Handbook\nReferences [Storage Architecture](/s/engineering/p/storage-architecture-123).",
                    ContentSha256: "def456",
                    UpdatedAtUtc: DateTimeOffset.UtcNow)
            };

            var dmResult = await connectors.SyncDocmostSpaceAsync(vault, "spc-docmost-arch", docmostRemote);
            Assert.Equal(ExternalConnectorKind.DocmostSpace, dmResult.ConnectorKind);
            Assert.Equal(1, dmResult.PulledCount);

            var syncedDmNote = vault.Notes.FirstOrDefault(n => n.Title == "Docmost Space Handbook");
            Assert.NotNull(syncedDmNote);
            Assert.Equal("spc-docmost-arch", syncedDmNote!.GetProperty("docmost_space_id"));
            Assert.Contains("[[Storage Architecture]]", syncedDmNote.BodyWithoutFrontmatter);

            string exportedDocmost = WorkspaceConnectorsEngine.ConvertRhyoliteToDocmostMarkdown(syncedDmNote.BodyWithoutFrontmatter, "engineering");
            Assert.Contains("[Storage Architecture](/s/engineering/p/storage-architecture)", exportedDocmost);
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
    public async Task EnterpriseDevExEngine_OnePassword_KeePassXc_Runbooks_CodeDrift_Scorecard_AndGitHubPrAnalysis_WorkEndToEnd()
    {
        var runbookNote = new VaultNote
        {
            Id = "runbook.md",
            RelativePath = "04 — Runbooks/Payment Service Runbook.md",
            FullPath = "/tmp/runbook.md",
            Title = "Payment Service Runbook",
            FolderPath = "04 — Runbooks",
            RawMarkdown = """
                ---
                owner: "@payments-sre-team"
                tier: tier-1
                status: Shipped
                ---
                # Payment Service Incident Runbook
                Links to [[Storage Architecture]] and code [[code:EnterpriseDevExEngine.cs#ScanAndRedactSecrets]] plus stale [[code:EnterpriseDevExEngine.cs#RemovedLegacyMethod]].

                ```bash env=prod-readonly
                curl -H "Authorization: Bearer ghp_1234567890abcdefghijklmnopqrstuvwx" -s https://payments.internal/healthz
                ```

                ```sql env=prod
                DROP TABLE payments_ledger;
                ```
                """
        };

        MarkdownAstEngine.ParseNoteMetadata(runbookNote);

        // 1. Shift-Left Secret Scanner & Redactor (1Password CLI + KeePassXC CLI)
        var opReport = EnterpriseDevExEngine.ScanAndRedactSecrets(runbookNote.RelativePath, runbookNote.RawMarkdown, SecretVaultBackend.OnePasswordCli);
        Assert.False(opReport.IsClean);
        Assert.Single(opReport.Findings);
        Assert.Equal("SEC-GH-01", opReport.Findings[0].RuleId);
        Assert.Contains("{{ op://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}", opReport.RedactedMarkdown);
        Assert.DoesNotContain("ghp_1234567890", opReport.RedactedMarkdown);

        var kpReport = EnterpriseDevExEngine.ScanAndRedactSecrets(runbookNote.RelativePath, runbookNote.RawMarkdown, SecretVaultBackend.KeePassXcCli);
        Assert.Contains("{{ keepassxc://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}", kpReport.RedactedMarkdown);

        // 2. Executable Runbook Cells, 1Password CLI Ephemeral Injection & Destructive Guardrail
        var cells = EnterpriseDevExEngine.ExtractRunbookCells(opReport.RedactedMarkdown);
        Assert.Equal(2, cells.Count);
        Assert.True(cells[0].IsReadOnlySafe);
        Assert.Equal("prod-readonly", cells[0].EnvironmentProfile);
        Assert.Single(cells[0].ReferencedSecretUris);

        var execSafe = await EnterpriseDevExEngine.ExecuteRunbookCellAsync(cells[0]);
        Assert.True(execSafe.Executed);
        Assert.False(execSafe.BlockedBySafetyGuardrail);
        Assert.Equal(1, execSafe.InjectedSecretsCount);
        Assert.Contains("op read", execSafe.OutputLog);

        var execBlocked = await EnterpriseDevExEngine.ExecuteRunbookCellAsync(cells[1]);
        Assert.False(execBlocked.Executed);
        Assert.True(execBlocked.BlockedBySafetyGuardrail);

        // 3. Docs-as-Code Symbol Drift Detection
        string tempRepo = Path.Combine(Path.GetTempPath(), "RhyoliteDriftTest_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tempRepo);
        try
        {
            File.WriteAllText(Path.Combine(tempRepo, "EnterpriseDevExEngine.cs"), "public static class EnterpriseDevExEngine { public static void ScanAndRedactSecrets() {} }");
            var driftResults = EnterpriseDevExEngine.DetectCodeDocumentationDrift(runbookNote, tempRepo);
            Assert.Equal(2, driftResults.Count);
            Assert.False(driftResults[0].IsDrifted); // ScanAndRedactSecrets exists
            Assert.True(driftResults[1].IsDrifted);  // RemovedLegacyMethod is drifted
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }

        // 4. IDP Service Operational Maturity Scorecard (before & after secret redaction)
        var scorecardWithSecret = EnterpriseDevExEngine.EvaluateServiceMaturity(runbookNote);
        Assert.Equal(75, scorecardWithSecret.ScoreOutOf100);
        Assert.Equal(ServiceMaturityTier.Silver, scorecardWithSecret.MaturityTier);

        runbookNote.RawMarkdown = opReport.RedactedMarkdown;
        MarkdownAstEngine.ParseNoteMetadata(runbookNote);
        var cleanScorecard = EnterpriseDevExEngine.EvaluateServiceMaturity(runbookNote);
        Assert.Equal(100, cleanScorecard.ScoreOutOf100);
        Assert.Equal(ServiceMaturityTier.Gold, cleanScorecard.MaturityTier);

        // 5. GitHub PR Preview, Comment Ingestion, Single-RTT GraphQL, WAL Cache Key & Bidirectional Reply/Resolve
        var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
        var prReport = EnterpriseDevExEngine.AnalyzeGitHubPullRequest(samplePr, [runbookNote]);
        Assert.Equal(142, prReport.PullRequest.Number);
        Assert.Equal(2, prReport.UnresolvedThreadsCount);
        Assert.Equal(1, prReport.ActionableBlockersCount);
        Assert.Contains("Payment Service Runbook", prReport.ImpactedVaultNotes);
        Assert.Contains("# PR #142:", prReport.GeneratedReviewMarkdown);

        string singleRttQuery = EnterpriseDevExEngine.BuildSingleRttPullRequestGraphQlQuery("dustinblad", "rhyolite", 142);
        Assert.Contains("reviewThreads(first: 100)", singleRttQuery);
        Assert.Contains("statusCheckRollup", singleRttQuery);

        string walCacheKey = EnterpriseDevExEngine.ComputePrSqliteWalCacheKey(samplePr.Repository, samplePr.Number, samplePr.HeadSha);
        Assert.Equal("pr_cache:dustinblad/rhyolite#142:9f4b2c8e1a7d305b", walCacheKey);

        string resolveMutation = EnterpriseDevExEngine.BuildBidirectionalPrThreadResolutionMutation("PRRT_kwDO101", "Fixed in 9f4b2c8 via 1Password CLI ephemeral injection.");
        Assert.Contains("addPullRequestReviewThreadReply", resolveMutation);
        Assert.Contains("resolveReviewThread", resolveMutation);
    }
}
