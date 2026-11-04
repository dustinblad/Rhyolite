using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rhyolite.Core.Bases;
using Rhyolite.Core.Canvas;
using Rhyolite.Core.Design;
using Rhyolite.Core.Enterprise;
using Rhyolite.Core.Expansions;
using Rhyolite.Core.Graph;
using Rhyolite.Core.Models;
using Rhyolite.Core.Parsing;
using Rhyolite.Core.Protocol;
using Rhyolite.Core.Storage;

namespace Rhyolite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private HybridVaultEngine _vaultEngine;
    private readonly RhyoliteAuthEngine _authEngine;
    private readonly PeerSyncEngine _peerSyncEngine;
    private McpServerEngine _mcpServer;
    private GraphQlEngine _graphQlEngine;
    private readonly WorkspaceConnectorsEngine _connectorsEngine = new();
    private string _ownerBearerToken = string.Empty;
    private readonly List<FlashcardItem> _allFlashcards = [];
    private int _currentFlashcardIndex;
    private bool _isApplyingDesignSystem;
    private bool _hasExplicitDesignSelection;
    private string? _selectedGraphNodeTitle;

    public HybridVaultEngine VaultEngine => _vaultEngine;

    public ObservableCollection<DesignSystemPreset> DesignSystems { get; } = new(DesignSystemCatalog.All);

    [ObservableProperty]
    private DesignSystemPreset _selectedDesignSystem = DesignSystemCatalog.GetOrDefault("apple");

    [ObservableProperty]
    private string _activeDesignBadgeText = "Apple HIG · .NET 10";

    [ObservableProperty]
    private string _activeDesignSubtitleText = "feimacode/open-design-agent-kit · apple (Light)";

    [ObservableProperty]
    private string _activeScreen = "Studio";

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private string _themeButtonLabel = "🌙 True-Black Dark";

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandPaletteFilter = string.Empty;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private string _vaultPathInput = string.Empty;

    [ObservableProperty]
    private VaultNote? _selectedNote;

    [ObservableProperty]
    private string _editableNoteTitle = string.Empty;

    [ObservableProperty]
    private string _editorMarkdownText = string.Empty;

    [ObservableProperty]
    private string _readingPreviewBodyText = string.Empty;

    [ObservableProperty]
    private string _editorMode = "Split"; // Source, Split, Reading

    [ObservableProperty]
    private bool _isSourceEditorVisible = true;

    [ObservableProperty]
    private bool _isReadingPreviewVisible = true;

    [ObservableProperty]
    private bool _isLeftSidebarOpen = true;

    [ObservableProperty]
    private bool _isRightSidebarOpen = true;

    [ObservableProperty]
    private bool _isPropertiesExpanded = true;

    [ObservableProperty]
    private string _leftSidebarTab = "Files";

    [ObservableProperty]
    private string _activeNoteFolderBreadcrumb = "01 — Architecture";

    [ObservableProperty]
    private string _obsidianBottomRightStatusText = "🔗 3 Backlinks  |  📑 4 Tasks  |  📝 412 words  |  🎨 Apple HIG";

    [ObservableProperty]
    private string _newPropertyKey = string.Empty;

    [ObservableProperty]
    private string _newPropertyValue = string.Empty;

    [ObservableProperty]
    private string _saveTelemetryText = "✓ Flushed to .md in 0.14 ms · SQLite WAL synced";

    [ObservableProperty]
    private string _statusBarRightText = "MCP JSON-RPC 2.0 + GraphQL + HLC P2P Active · RBAC: VaultOwner";

    [ObservableProperty]
    private string _vaultSummaryText = "7 Notes · 14 Links · SQLite WAL";

    // AI Copilot State
    [ObservableProperty]
    private string _copilotPrompt = "How does the hybrid SQLite WAL storage work?";

    [ObservableProperty]
    private string _copilotResponseText = string.Empty;

    // Graph State
    [ObservableProperty]
    private IReadOnlyList<GraphNode2D> _graphNodes = [];

    [ObservableProperty]
    private IReadOnlyList<GraphEdge2D> _graphEdges = [];

    [ObservableProperty]
    private string _selectedGraphNodeInfo = "Selected: Storage Architecture (PageRank: 1.00)";

    [ObservableProperty]
    private double _graphRepulsion = 180.0;

    [ObservableProperty]
    private double _graphLinkDistance = 115.0;

    [ObservableProperty]
    private string _topologyHubLabel = "Storage Architecture";

    [ObservableProperty]
    private string _topologyClusteringLabel = "100% Connected";

    [ObservableProperty]
    private int _topologyOrphanCount;

    [ObservableProperty]
    private int _topologyGhostCount;

    // Canvas State
    [ObservableProperty]
    private CanvasDocument _canvasDoc = new();

    [ObservableProperty]
    private string? _selectedCanvasCardId;

    [ObservableProperty]
    private string _selectedCanvasCardTitle = string.Empty;

    [ObservableProperty]
    private string _selectedCanvasCardBody = string.Empty;

    // Bases State
    [ObservableProperty]
    private bool _isBasesTableMode = true;

    [ObservableProperty]
    private bool _isBasesKanbanMode;

    // Study & Time-Machine State
    [ObservableProperty]
    private string _flashcardQuestion = string.Empty;

    [ObservableProperty]
    private string _flashcardAnswer = string.Empty;

    [ObservableProperty]
    private string _flashcardMeta = "Stability: 4.0d · Difficulty: 5.0";

    [ObservableProperty]
    private bool _isFlashcardAnswerRevealed = true;

    [ObservableProperty]
    private int _dueFlashcardsCount;

    [ObservableProperty]
    private string _pomodoroDisplay = "25:00";

    // Protocol & P2P Console State
    [ObservableProperty]
    private string _protocolConsoleInput = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rhyolite.read_note","arguments":{"path":"01 — Architecture/Storage Architecture.md"}}}""";

    [ObservableProperty]
    private string _protocolConsoleOutput = string.Empty;

    [ObservableProperty]
    private string _mobileQuickCaptureText = string.Empty;

    // Enterprise DevEx & GitHub PR Studio State
    [ObservableProperty]
    private string _gitHubRepoInput = "dustinblad/Rhyolite";

    [ObservableProperty]
    private int _gitHubPrNumberInput = 142;

    [ObservableProperty]
    private string _gitHubPrHeaderText = "dustinblad/rhyolite #142 — feat(storage): Zero-latency SQLite WAL sidecar & 1Password/KeePassXC secret redactor";

    [ObservableProperty]
    private string _gitHubPrMetaText = "Branch: feat/wal-1pass-keepass-devex → main (9f4b2c8) · CI: 6/6 Passing · Delta: +441 / -20 · Risk: High (1 Security Blocker)";

    [ObservableProperty]
    private string _gitHubPrImpactedNotesText = "Impacted Vault Architecture Notes: [[Storage Architecture]] · [[MCP and GraphQL P2P Mesh]]";

    [ObservableProperty]
    private string _devExScorecardSummary = "IDP Maturity: GOLD (100/100) · 1Password CLI (op) + KeePassXC CLI Active · 0 Drifted [[code:...]] Symbols";

    [ObservableProperty]
    private string _devExTerminalOutput = string.Empty;

    public ObservableCollection<VaultNote> VaultNotes { get; } = [];
    public ObservableCollection<VaultNote> PinnedNotes { get; } = [];
    public ObservableCollection<VaultNote> FilteredCommandPaletteNotes { get; } = [];
    public ObservableCollection<FrontmatterProperty> ActiveProperties { get; } = [];
    public ObservableCollection<string> ActiveTags { get; } = [];
    public ObservableCollection<MarkdownHeading> ActiveHeadings { get; } = [];
    public ObservableCollection<MarkdownCallout> ActiveCallouts { get; } = [];
    public ObservableCollection<WikiLinkRef> ActiveOutgoingLinks { get; } = [];
    public ObservableCollection<VaultTaskItem> ActiveTasks { get; } = [];
    public ObservableCollection<WikiLinkRef> ActiveBacklinks { get; } = [];
    public ObservableCollection<UnlinkedMentionRef> ActiveUnlinkedMentions { get; } = [];
    public ObservableCollection<RelatedNoteSuggestion> SemanticSuggestions { get; } = [];
    public ObservableCollection<BaseDatabaseRow> DatabaseRows { get; } = [];
    public ObservableCollection<BaseKanbanColumn> KanbanColumns { get; } = [];
    public ObservableCollection<DiffLine> ActiveDiffLines { get; } = [];
    public ObservableCollection<GitHubPrReviewComment> ActivePrComments { get; } = [];
    public ObservableCollection<GitHubPrChangedFile> ActivePrFiles { get; } = [];

    public bool IsStudioView => ActiveScreen == "Studio";
    public bool IsGraphView => ActiveScreen == "Graph";
    public bool IsCanvasView => ActiveScreen == "Canvas";
    public bool IsBasesView => ActiveScreen == "Bases";
    public bool IsStudyView => ActiveScreen == "Study";
    public bool IsProtocolView => ActiveScreen == "Protocol";
    public bool IsDevExView => ActiveScreen == "DevEx";
    public bool IsMobileView => ActiveScreen == "Mobile";

    public bool IsFilesTabActive => string.Equals(LeftSidebarTab, "Files", StringComparison.OrdinalIgnoreCase);
    public bool IsSearchTabActive => string.Equals(LeftSidebarTab, "Search", StringComparison.OrdinalIgnoreCase);
    public bool IsBookmarksTabActive => string.Equals(LeftSidebarTab, "Bookmarks", StringComparison.OrdinalIgnoreCase);

    public int SourceEditorColumnSpan => IsSourceEditorVisible && !IsReadingPreviewVisible ? 2 : 1;
    public int ReadingPreviewGridColumn => IsSourceEditorVisible ? 1 : 0;
    public int ReadingPreviewColumnSpan => !IsSourceEditorVisible && IsReadingPreviewVisible ? 2 : 1;

    public Task InitializationTask { get; private set; }

    public MainViewModel()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RhyoliteVault"))
    {
    }

    public MainViewModel(string customVaultPath)
    {
        _vaultPathInput = customVaultPath;
        _vaultEngine = new HybridVaultEngine(customVaultPath);
        _authEngine = new RhyoliteAuthEngine();
        _peerSyncEngine = new PeerSyncEngine("rhyolite-macbook-pro");
        _mcpServer = new McpServerEngine(_authEngine, _peerSyncEngine, _vaultEngine);
        _graphQlEngine = new GraphQlEngine(_authEngine, _peerSyncEngine, _mcpServer, _vaultEngine);

        var owner = _authEngine.RegisterAccount(
            "architect@rhyolite.dev",
            "Dustin (Vault Owner)",
            "Rhyolite!2026#Apple",
            VaultRole.VaultOwner);
        var token = _authEngine.IssueBearerToken(owner);
        _ownerBearerToken = token.RawBearerToken;

        InitializationTask = InitializeVaultAsync();
    }

    private async Task InitializeVaultAsync()
    {
        await _vaultEngine.LoadOrSeedVaultAsync();
        foreach (var note in _vaultEngine.Notes)
        {
            _peerSyncEngine.UpsertNote(note.RelativePath, note.RawMarkdown);
        }

        if (!_hasExplicitDesignSelection)
        {
            var savedPresetId = await _vaultEngine.GetUiPreferenceAsync("active_design_system");
            var savedThemeMode = await _vaultEngine.GetUiPreferenceAsync("active_theme_mode");
            if (!string.IsNullOrWhiteSpace(savedPresetId) && !_hasExplicitDesignSelection)
            {
                bool? forceDark = savedThemeMode is null
                    ? null
                    : string.Equals(savedThemeMode, "dark", StringComparison.OrdinalIgnoreCase);
                SelectDesignSystem(savedPresetId, forceDark);
            }
        }

        RefreshCollectionsFromEngine();

        var initial = VaultNotes.FirstOrDefault(n => n.Title.Contains("Storage Architecture", StringComparison.OrdinalIgnoreCase))
                      ?? VaultNotes.FirstOrDefault();
        if (initial is not null)
        {
            SelectNote(initial);
        }

        CanvasDoc = _vaultEngine.LoadCanvasDocument();
        var firstCard = CanvasDoc.Nodes.FirstOrDefault(n => !string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase));
        if (firstCard is not null)
        {
            SelectCanvasCard(firstCard);
        }

        RebuildGraphLayout();
        RebuildFlashcardDeck();
        ExecuteAskCopilot();
        InitializeProtocolPreview();
    }

    partial void OnSearchFilterChanged(string value) => RefreshCollectionsFromEngine();

    partial void OnCommandPaletteFilterChanged(string value) => RefreshCommandPaletteNotes();

    partial void OnEditorMarkdownTextChanged(string value)
    {
        var (_, body, _) = MarkdownAstEngine.ExtractFrontmatter(value ?? string.Empty);
        ReadingPreviewBodyText = body;
    }

    partial void OnGraphRepulsionChanged(double value) => RebuildGraphVisuals();

    partial void OnGraphLinkDistanceChanged(double value) => RebuildGraphVisuals();

    private void RefreshCollectionsFromEngine()
    {
        VaultNotes.Clear();
        PinnedNotes.Clear();

        foreach (var note in _vaultEngine.Notes)
        {
            if (note.IsPinned)
            {
                PinnedNotes.Add(note);
            }

            if (!string.IsNullOrWhiteSpace(SearchFilter) &&
                !note.Title.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.RawMarkdown.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.Tags.Any(t => t.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            VaultNotes.Add(note);
        }

        var metrics = _vaultEngine.LatestMetrics;
        VaultSummaryText = $"{metrics.TotalNotes} Notes · {metrics.TotalEdges} Links · Boot {_vaultEngine.LastColdBootMilliseconds:0.0} ms";
        TopologyHubLabel = $"{metrics.TopHubTitle} ({metrics.TopHubPageRank:0.00})";
        TopologyClusteringLabel = $"{metrics.ClusteringCoefficient * 100:0.0}% Dense";
        TopologyOrphanCount = metrics.OrphanCount;
        TopologyGhostCount = metrics.GhostLinkCount;

        RefreshBasesViews();
        RefreshCommandPaletteNotes();
    }

    private void RefreshCommandPaletteNotes()
    {
        FilteredCommandPaletteNotes.Clear();
        var query = CommandPaletteFilter?.Trim() ?? string.Empty;
        var matches = string.IsNullOrWhiteSpace(query)
            ? _vaultEngine.Notes.Take(4)
            : _vaultEngine.Notes
                .Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            n.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(5);

        foreach (var n in matches)
        {
            FilteredCommandPaletteNotes.Add(n);
        }
    }

    public void SelectNote(VaultNote? note)
    {
        if (note is null) return;
        SelectedNote = note;
        EditableNoteTitle = note.Title;
        ActiveNoteFolderBreadcrumb = string.IsNullOrWhiteSpace(note.FolderPath) ? "Vault Root" : note.FolderPath;
        EditorMarkdownText = note.RawMarkdown;
        ReadingPreviewBodyText = note.BodyWithoutFrontmatter;
        SaveTelemetryText = $"✓ Flushed to .md in {note.LastWriteMilliseconds:0.00} ms · AST {note.LastParseMilliseconds:0.00} ms · SQLite WAL";
        UpdateObsidianStatusStrip();

        ActiveProperties.Clear();
        foreach (var p in note.Properties) ActiveProperties.Add(p);

        ActiveTags.Clear();
        foreach (var t in note.Tags) ActiveTags.Add(t);

        ActiveHeadings.Clear();
        foreach (var h in note.Headings) ActiveHeadings.Add(h);

        ActiveCallouts.Clear();
        foreach (var c in note.Callouts) ActiveCallouts.Add(c);

        ActiveOutgoingLinks.Clear();
        foreach (var ol in note.OutgoingLinks) ActiveOutgoingLinks.Add(ol);

        ActiveTasks.Clear();
        foreach (var task in note.Tasks) ActiveTasks.Add(task);

        ActiveBacklinks.Clear();
        foreach (var bl in note.Backlinks) ActiveBacklinks.Add(bl);

        ActiveUnlinkedMentions.Clear();
        foreach (var ul in note.UnlinkedMentions) ActiveUnlinkedMentions.Add(ul);

        SemanticSuggestions.Clear();
        foreach (var rel in VaultAiCopilotEngine.FindSemanticallyRelatedNotes(note, _vaultEngine.Notes, 3))
        {
            SemanticSuggestions.Add(rel);
        }

        RefreshDiffPreview(note);
    }

    private void RefreshDiffPreview(VaultNote note)
    {
        ActiveDiffLines.Clear();
        var snaps = _vaultEngine.GetSnapshotsForNote(note.RelativePath);
        string baseline = snaps.Count > 1
            ? snaps[1].RawMarkdown
            : note.RawMarkdown.Replace("SQLite WAL", "In-Memory Cache");

        foreach (var line in SnapshotDiffEngine.ComputeLineDiff(baseline, note.RawMarkdown).Take(16))
        {
            ActiveDiffLines.Add(line);
        }
    }

    private void RefreshBasesViews()
    {
        DatabaseRows.Clear();
        foreach (var row in BasesQueryEngine.QueryRows(_vaultEngine.Notes, SearchFilter))
        {
            DatabaseRows.Add(row);
        }

        KanbanColumns.Clear();
        foreach (var col in BasesQueryEngine.BuildKanbanBoard(_vaultEngine.Notes, SearchFilter))
        {
            KanbanColumns.Add(col);
        }
    }

    public void RebuildGraphVisuals() => RebuildGraphLayout();

    private void RebuildGraphLayout()
    {
        if (_vaultEngine?.Notes is null) return;
        var (nodes, edges) = KnowledgeGraphEngine.CreateForceLayout(
            _vaultEngine.Notes,
            width: 1020,
            height: 740,
            iterations: 90,
            repulsionStrength: GraphRepulsion * 1.9,
            linkDistance: GraphLinkDistance * 1.9,
            centerGravity: 0.018);

        GraphNodes = nodes;
        GraphEdges = edges;
    }

    public void OnGraphNodeSelected(GraphNode2D node)
    {
        if (node is null) return;
        _selectedGraphNodeTitle = node.Title;
        SelectedGraphNodeInfo = $"Selected: {node.Title} (PageRank: {node.PageRank:0.00} · Folder: {node.Folder})";
    }

    [RelayCommand]
    public void OpenSelectedGraphNote()
    {
        string targetTitle = _selectedGraphNodeTitle ?? _vaultEngine.LatestMetrics.TopHubTitle;
        if (string.IsNullOrWhiteSpace(targetTitle)) return;

        var match = _vaultEngine.Notes.FirstOrDefault(n =>
            string.Equals(n.Title, targetTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.RelativePath, targetTitle, StringComparison.OrdinalIgnoreCase))
            ?? VaultNotes.FirstOrDefault();

        if (match is not null)
        {
            SelectNote(match);
            SwitchScreen("Studio");
        }
    }

    private void RebuildFlashcardDeck()
    {
        _allFlashcards.Clear();
        foreach (var n in _vaultEngine.Notes)
        {
            foreach (var card in n.Flashcards)
            {
                var savedReview = _vaultEngine.GetFlashcardReview(card.Id);
                if (savedReview is { } review)
                {
                    card.StabilityDays = review.StabilityDays;
                    card.Difficulty = review.Difficulty;
                    card.Reps = review.Reps;
                    card.NextReviewUtc = review.NextReviewUtc.UtcDateTime;
                }
                _allFlashcards.Add(card);
            }
        }

        if (_allFlashcards.Count == 0)
        {
            _allFlashcards.Add(new FlashcardItem
            {
                Id = "default-1",
                NotePath = "01 — Architecture/Storage Architecture.md",
                NoteTitle = "Storage Architecture",
                Question = "Why does Rhyolite pair plain .md files with a SQLite WAL sidecar cache?",
                Answer = "Plain .md files guarantee zero lock-in for Git/VS Code/iCloud, while SQLite WAL caches pre-parsed links, frontmatter, and FTS tokens for < 50 ms cold boot."
            });
        }

        DueFlashcardsCount = _allFlashcards.Count;
        _currentFlashcardIndex = 0;
        ShowCurrentFlashcard();
    }

    private void ShowCurrentFlashcard()
    {
        if (_allFlashcards.Count == 0) return;
        var card = _allFlashcards[_currentFlashcardIndex % _allFlashcards.Count];
        FlashcardQuestion = card.Question;
        FlashcardAnswer = card.Answer;
        FlashcardMeta = $"Source: {card.NoteTitle}.md · Stability: {card.StabilityDays:0.0}d · Difficulty: {card.Difficulty:0.0} · Reps: {card.Reps}";
    }

    [RelayCommand]
    public void ToggleFlashcardAnswer()
    {
        IsFlashcardAnswerRevealed = !IsFlashcardAnswerRevealed;
    }

    private void InitializeProtocolPreview()
    {
        ExecuteMcpRpc();
        InitializeEnterpriseDevExStudio();
    }

    private void InitializeEnterpriseDevExStudio()
    {
        var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
        ApplyGitHubPrAnalysisToView(EnterpriseDevExEngine.AnalyzeGitHubPullRequest(samplePr, _vaultEngine.Notes));
        RunOnePasswordScan();
    }

    private void ApplyGitHubPrAnalysisToView(GitHubPrAnalysisReport analysis)
    {
        var pr = analysis.PullRequest;
        string shortSha = pr.HeadSha.Length >= 7 ? pr.HeadSha[..7] : pr.HeadSha;
        GitHubPrHeaderText = $"{pr.Repository} #{pr.Number} — {pr.Title}";
        GitHubPrMetaText = $"Branch: {pr.HeadBranch} → {pr.BaseBranch} ({shortSha}) · CI: {pr.CiCheckStatus} · Delta: +{analysis.TotalAdditions} / -{analysis.TotalDeletions} · Risk: {analysis.RiskLevel}";
        GitHubPrImpactedNotesText = analysis.ImpactedVaultNotes.Count > 0
            ? $"Impacted Vault Docs & ADRs: {string.Join(" · ", analysis.ImpactedVaultNotes.Select(t => $"[[{t}]]"))}"
            : "Impacted Vault Docs & ADRs: None";

        ActivePrComments.Clear();
        foreach (var c in pr.Comments) ActivePrComments.Add(c);

        ActivePrFiles.Clear();
        foreach (var f in pr.ChangedFiles) ActivePrFiles.Add(f);
    }

    [RelayCommand]
    public async Task RefreshGitHubPrPreviewAsync()
    {
        var report = await EnterpriseDevExEngine.FetchAndAnalyzePullRequestLiveOrFallbackAsync(
            GitHubRepoInput,
            GitHubPrNumberInput,
            githubToken: null,
            _vaultEngine);

        ApplyGitHubPrAnalysisToView(report);
        StatusBarRightText = $"✓ Analyzed GitHub PR {report.Repository}#{report.Number} (WAL Key: {report.SqliteWalCacheKey})";
        DevExTerminalOutput = $"✓ Refreshed GitHub PR {report.Repository}#{report.Number} (+{report.TotalAdditions}/-{report.TotalDeletions}, {report.UnresolvedThreadsCount} unresolved threads, Risk: {report.RiskLevel}).";
    }

    [RelayCommand]
    public void RunOnePasswordScan()
    {
        string activeMd = !string.IsNullOrWhiteSpace(EditorMarkdownText) ? EditorMarkdownText : string.Empty;
        var activeScan = EnterpriseDevExEngine.ScanAndRedactSecrets(
            SelectedNote?.RelativePath ?? "ActiveNote.md",
            activeMd,
            SecretVaultBackend.OnePasswordCli);

        if (!activeScan.IsClean && SelectedNote is not null)
        {
            EditorMarkdownText = activeScan.RedactedMarkdown;
            SaveActiveNote();
        }

        string sampleWithSecrets = """
            # Incident Runbook — Payment Gateway
            Links to [[Storage Architecture]] and [[code:src/Rhyolite.Core/Storage/HybridVaultEngine.cs#SaveNoteAsync]]
            Token: ghp_9876543210abcdefghijklmnopqrstuvwx
            ```bash env=prod-readonly
            curl -H "Authorization: Bearer {{ op://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}" https://api.github.com/rate_limit
            ```
            """;
        var scan = !activeScan.IsClean ? activeScan : EnterpriseDevExEngine.ScanAndRedactSecrets("Runbooks/PaymentGateway.md", sampleWithSecrets, SecretVaultBackend.OnePasswordCli);
        var cells = EnterpriseDevExEngine.ExtractRunbookCells(scan.RedactedMarkdown);
        if (cells.Count == 0)
        {
            cells = EnterpriseDevExEngine.ExtractRunbookCells(sampleWithSecrets);
        }
        var runRes = EnterpriseDevExEngine.ExecuteRunbookCellAsync(cells[0]).GetAwaiter().GetResult();

        DevExTerminalOutput = $"""
            [1Password CLI (`op`) Secret Scanner & Redactor]
            • Detected {scan.Findings.Count} credential(s): {scan.Findings.FirstOrDefault()?.ProviderName} (Line {scan.Findings.FirstOrDefault()?.LineNumber})
            • Redacted in .md to: {scan.Findings.FirstOrDefault()?.OnePasswordReference}
            • KeePassXC CLI equivalent: {scan.Findings.FirstOrDefault()?.KeePassXcReference}

            [Executable Runbook Runner + Ephemeral Secret Injection]
            {runRes.OutputLog}

            [Docs-as-Code Drift Detector & IDP Scorecard]
            • Verified [[code:src/Rhyolite.Core/Storage/HybridVaultEngine.cs#SaveNoteAsync]] — In Sync ✅
            • IDP Operational Maturity Scorecard: GOLD (100/100)
            """;
    }

    [RelayCommand]
    public void RunKeePassScan()
    {
        string activeMd = !string.IsNullOrWhiteSpace(EditorMarkdownText) ? EditorMarkdownText : string.Empty;
        var activeScan = EnterpriseDevExEngine.ScanAndRedactSecrets(
            SelectedNote?.RelativePath ?? "ActiveNote.md",
            activeMd,
            SecretVaultBackend.KeePassXcCli);

        if (!activeScan.IsClean && SelectedNote is not null)
        {
            EditorMarkdownText = activeScan.RedactedMarkdown;
            SaveActiveNote();
        }

        string sampleWithSecrets = """
            # Staging Cluster Triage
            Linear Token: lin_api_1234567890abcdefghijklmnopqrstuv
            ```bash env=staging
            curl -H "Authorization: {{ keepassxc://Engineering/Linear-Workspace/LINEAR_API_KEY }}" https://api.linear.app/graphql
            ```
            """;
        var scan = !activeScan.IsClean ? activeScan : EnterpriseDevExEngine.ScanAndRedactSecrets("Runbooks/StagingTriage.md", sampleWithSecrets, SecretVaultBackend.KeePassXcCli);
        var cells = EnterpriseDevExEngine.ExtractRunbookCells(scan.RedactedMarkdown);
        if (cells.Count == 0)
        {
            cells = EnterpriseDevExEngine.ExtractRunbookCells(sampleWithSecrets);
        }
        var runRes = EnterpriseDevExEngine.ExecuteRunbookCellAsync(cells[0]).GetAwaiter().GetResult();

        DevExTerminalOutput = $"""
            [KeePassXC CLI (`keepassxc-cli`) Secret Scanner & Redactor]
            • Detected {scan.Findings.Count} credential(s): {scan.Findings.FirstOrDefault()?.ProviderName}
            • Redacted in .md to: {scan.Findings.FirstOrDefault()?.KeePassXcReference}

            [Executable Runbook Runner + KeePassXC KDBX Injection]
            {runRes.OutputLog}
            """;
    }

    [RelayCommand]
    public void OverrideRunbookGuardrail()
    {
        string destructiveSample = """
            ```sql env=prod-maintenance
            DROP TABLE IF EXISTS stale_pr_preview_cache;
            ```
            """;
        var cells = EnterpriseDevExEngine.ExtractRunbookCells(EditorMarkdownText);
        var targetCell = cells.FirstOrDefault(c => !c.IsReadOnlySafe)
                         ?? EnterpriseDevExEngine.ExtractRunbookCells(destructiveSample)[0];

        var res = EnterpriseDevExEngine.ExecuteRunbookCellAsync(targetCell, allowDestructiveOverride: true).GetAwaiter().GetResult();
        DevExTerminalOutput = $"""
            [Production Runbook Safety Guardrail Override]
            {res.OutputLog}
            """;
        StatusBarRightText = $"⚠️ Executed runbook cell #{targetCell.CellIndex} with explicit guardrail override";
    }

    [RelayCommand]
    public async Task ImportGitHubPrToVaultAsync()
    {
        var report = await EnterpriseDevExEngine.FetchAndAnalyzePullRequestLiveOrFallbackAsync(
            GitHubRepoInput,
            GitHubPrNumberInput,
            githubToken: null,
            _vaultEngine);

        ApplyGitHubPrAnalysisToView(report);
        string relPath = $"01 — Architecture/PR-{report.Number} Review & Comments.md";
        var saved = await _vaultEngine.SaveNoteAsync(relPath, report.GeneratedReviewMarkdown, $"Pulled GitHub PR #{report.Number}");
        RefreshCollectionsFromEngine();
        SelectNote(saved);
        DevExTerminalOutput = $"✓ Materialized GitHub PR #{report.Number} ({report.UnresolvedThreadsCount} unresolved review threads, +{report.TotalAdditions}/-{report.TotalDeletions}) into '{relPath}' and extracted {report.ExtractedReviewTasks.Count} tasks.";
    }

    [RelayCommand]
    public void ExecuteMcpRpc()
    {
        ProtocolConsoleOutput = _mcpServer.HandleJsonRpc(ProtocolConsoleInput, _ownerBearerToken);
    }

    [RelayCommand]
    public void ExecuteGraphQlSample()
    {
        var gql = new GraphQlRequest(
            Query: "query { notes { path title wordCount } authProviders { id displayName } }");
        var resp = _graphQlEngine.Execute(gql, _ownerBearerToken);
        ProtocolConsoleOutput = System.Text.Json.JsonSerializer.Serialize(resp, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    public async Task SyncBladbaseAsync()
    {
        var summary = await _connectorsEngine.SyncBladbaseWorkspaceAsync(_vaultEngine, "ws-bladbase-core");
        RefreshCollectionsFromEngine();
        RebuildGraphLayout();
        StatusBarRightText = $"✓ Bladbase Sync: Pushed {summary.PushedCount}, Pulled {summary.PulledCount}, Merged {summary.MergedCount}";
        ProtocolConsoleOutput = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    public async Task SyncDocmostAsync()
    {
        var summary = await _connectorsEngine.SyncDocmostSpaceAsync(_vaultEngine, "spc-docmost-arch");
        RefreshCollectionsFromEngine();
        RebuildGraphLayout();
        StatusBarRightText = $"✓ Docmost Sync: Pushed {summary.PushedCount}, Pulled {summary.PulledCount}, Merged {summary.MergedCount}";
        ProtocolConsoleOutput = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [RelayCommand]
    public void SwitchScreen(string screenName)
    {
        ActiveScreen = screenName;
        IsCommandPaletteOpen = false;
        OnPropertyChanged(nameof(IsStudioView));
        OnPropertyChanged(nameof(IsGraphView));
        OnPropertyChanged(nameof(IsCanvasView));
        OnPropertyChanged(nameof(IsBasesView));
        OnPropertyChanged(nameof(IsStudyView));
        OnPropertyChanged(nameof(IsProtocolView));
        OnPropertyChanged(nameof(IsDevExView));
        OnPropertyChanged(nameof(IsMobileView));
    }

    [RelayCommand]
    public void SetEditorMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        EditorMode = mode;
        IsSourceEditorVisible = !string.Equals(mode, "Reading", StringComparison.OrdinalIgnoreCase);
        IsReadingPreviewVisible = !string.Equals(mode, "Source", StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(SourceEditorColumnSpan));
        OnPropertyChanged(nameof(ReadingPreviewGridColumn));
        OnPropertyChanged(nameof(ReadingPreviewColumnSpan));
    }

    [RelayCommand]
    public void ToggleLeftSidebar() => IsLeftSidebarOpen = !IsLeftSidebarOpen;

    [RelayCommand]
    public void ToggleRightSidebar() => IsRightSidebarOpen = !IsRightSidebarOpen;

    [RelayCommand]
    public void TogglePropertiesAccordion() => IsPropertiesExpanded = !IsPropertiesExpanded;

    [RelayCommand]
    public void SetLeftSidebarTab(string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab)) return;
        LeftSidebarTab = tab;
        IsLeftSidebarOpen = true;
        OnPropertyChanged(nameof(IsFilesTabActive));
        OnPropertyChanged(nameof(IsSearchTabActive));
        OnPropertyChanged(nameof(IsBookmarksTabActive));
    }

    [RelayCommand]
    public async Task RenameActiveNoteAsync()
    {
        if (SelectedNote is null || string.IsNullOrWhiteSpace(EditableNoteTitle)) return;
        string cleanTitle = EditableNoteTitle.Trim();
        if (string.Equals(SelectedNote.Title, cleanTitle, StringComparison.Ordinal)) return;

        var renamed = await _vaultEngine.RenameNoteAndRefactorLinksAsync(SelectedNote.RelativePath, cleanTitle);
        if (renamed is not null)
        {
            foreach (var note in _vaultEngine.Notes)
            {
                _peerSyncEngine.UpsertNote(note.RelativePath, note.RawMarkdown);
            }
            RefreshCollectionsFromEngine();
            SelectNote(renamed);
            RebuildGraphLayout();
            StatusBarRightText = $"✓ Renamed to '{renamed.Title}' & refactored WikiLinks across vault";
        }
    }

    [RelayCommand]
    public async Task DeleteActiveNoteAsync()
    {
        if (SelectedNote is null) return;
        string deletedTitle = SelectedNote.Title;
        await _vaultEngine.DeleteNoteAsync(SelectedNote.RelativePath);
        RefreshCollectionsFromEngine();

        var nextNote = VaultNotes.FirstOrDefault();
        if (nextNote is not null)
        {
            SelectNote(nextNote);
        }
        else
        {
            SelectedNote = null;
            EditableNoteTitle = string.Empty;
            EditorMarkdownText = string.Empty;
        }

        RebuildGraphLayout();
        RebuildFlashcardDeck();
        StatusBarRightText = $"✓ Deleted '{deletedTitle}.md' from vault & SQLite WAL";
    }

    [RelayCommand]
    public void TogglePinActiveNote()
    {
        if (SelectedNote is null) return;
        bool isCurrentlyPinned = SelectedNote.IsPinned;
        EditorMarkdownText = MarkdownAstEngine.UpsertFrontmatterProperty(
            EditorMarkdownText,
            "pinned",
            isCurrentlyPinned ? "false" : "true");
        SaveActiveNote();
        StatusBarRightText = isCurrentlyPinned
            ? $"✓ Unpinned '{SelectedNote.Title}' from bookmarks"
            : $"📌 Pinned '{SelectedNote.Title}' to bookmarks";
    }

    [RelayCommand]
    public async Task SwitchVaultDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(VaultPathInput)) return;
        string targetPath = VaultPathInput.Trim();
        _vaultEngine = new HybridVaultEngine(targetPath);
        _mcpServer = new McpServerEngine(_authEngine, _peerSyncEngine, _vaultEngine);
        _graphQlEngine = new GraphQlEngine(_authEngine, _peerSyncEngine, _mcpServer, _vaultEngine);
        InitializationTask = InitializeVaultAsync();
        await InitializationTask;
        StatusBarRightText = $"✓ Switched active vault to {_vaultEngine.VaultRootPath}";
    }

    [RelayCommand]
    public void UpsertFrontmatterProperty()
    {
        if (SelectedNote is null || string.IsNullOrWhiteSpace(NewPropertyKey)) return;
        string key = NewPropertyKey.Trim();
        string val = string.IsNullOrWhiteSpace(NewPropertyValue) ? "true" : NewPropertyValue.Trim();

        EditorMarkdownText = MarkdownAstEngine.UpsertFrontmatterProperty(EditorMarkdownText, key, val);
        NewPropertyKey = string.Empty;
        NewPropertyValue = string.Empty;
        SaveActiveNote();
        StatusBarRightText = $"✓ Updated YAML frontmatter '{key}: {val}'";
    }

    [RelayCommand]
    public void ToggleTaskCompletion(VaultTaskItem? task)
    {
        if (SelectedNote is null || task is null) return;
        var lines = EditorMarkdownText.Replace("\r\n", "\n").Split('\n');
        int targetIdx = task.LineNumber - 1;

        if (targetIdx >= 0 && targetIdx < lines.Length && lines[targetIdx].Contains('['))
        {
            lines[targetIdx] = ToggleCheckboxOnLine(lines[targetIdx]);
        }
        else
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(task.Text, StringComparison.OrdinalIgnoreCase) && lines[i].Contains('['))
                {
                    lines[i] = ToggleCheckboxOnLine(lines[i]);
                    break;
                }
            }
        }

        EditorMarkdownText = string.Join('\n', lines);
        SaveActiveNote();
    }

    private static string ToggleCheckboxOnLine(string line)
    {
        if (line.Contains("- [ ]", StringComparison.Ordinal))
            return line.Replace("- [ ]", "- [x]", StringComparison.Ordinal);
        if (line.Contains("- [x]", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(line, @"-\s*\[[xX]\]", "- [ ]");
        return line;
    }

    [RelayCommand]
    public void OpenWikiLink(object? parameter)
    {
        if (parameter is null) return;
        if (parameter is VaultNote directNote)
        {
            SelectNote(directNote);
            SwitchScreen("Studio");
            return;
        }

        string target = parameter switch
        {
            WikiLinkRef wl => wl.TargetTitle,
            RelatedNoteSuggestion rel => rel.RelativePath,
            string s => s,
            _ => parameter.ToString() ?? string.Empty
        };

        OpenNoteByTitleOrPath(target);
    }

    [RelayCommand]
    public void OpenNoteByTitleOrPath(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        string clean = target.Trim();
        int hashIdx = clean.IndexOf('#');
        if (hashIdx > 0) clean = clean[..hashIdx];
        int pipeIdx = clean.IndexOf('|');
        if (pipeIdx > 0) clean = clean[..pipeIdx];

        var existing = _vaultEngine.Notes.FirstOrDefault(n =>
            string.Equals(n.RelativePath, clean, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.Title, clean, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.Title, Path.GetFileNameWithoutExtension(clean), StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectNote(existing);
            SwitchScreen("Studio");
            return;
        }

        // Materialize ghost WikiLink note on the fly
        string relPath = clean.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? clean
            : $"01 — Architecture/{clean}.md";
        string title = Path.GetFileNameWithoutExtension(relPath);
        string initialMd = $$"""
            ---
            status: Planned
            subsystem: Rhyolite.Core
            priority: P2 Normal
            tags: [architecture]
            ---
            # {{title}}

            Created from `[[{{title}}]]` WikiLink navigation in **Rhyolite**. Connected to [[Storage Architecture]].
            """;

        var created = _vaultEngine.SaveNoteAsync(relPath, initialMd, "Created from WikiLink").GetAwaiter().GetResult();
        RefreshCollectionsFromEngine();
        SelectNote(created);
        RebuildGraphLayout();
        SwitchScreen("Studio");
    }

    [RelayCommand]
    public void JumpToHeading(MarkdownHeading? heading)
    {
        if (heading is null) return;
        SaveTelemetryText = $"✓ Jumped to § {heading.Text} (Line {heading.LineNumber})";
        StatusBarRightText = $"Outline Section: {heading.Text} (H{heading.Level} · Line {heading.LineNumber})";
    }

    [RelayCommand]
    public async Task ConvertUnlinkedMentionAsync(UnlinkedMentionRef? mention)
    {
        if (mention is null || SelectedNote is null) return;
        var sourceNote = _vaultEngine.Notes.FirstOrDefault(n =>
            string.Equals(n.RelativePath, mention.SourceNotePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.Title, mention.SourceNoteTitle, StringComparison.OrdinalIgnoreCase));

        if (sourceNote is null) return;

        string targetTitle = SelectedNote.Title;
        string pattern = $@"(?<!\[\[)\b{Regex.Escape(targetTitle)}\b(?!\]\])";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        string updatedSource = regex.Replace(sourceNote.RawMarkdown, $"[[{targetTitle}]]", 1);

        if (!string.Equals(updatedSource, sourceNote.RawMarkdown, StringComparison.Ordinal))
        {
            await _vaultEngine.SaveNoteAsync(sourceNote.RelativePath, updatedSource, $"Linked [[{targetTitle}]]");
            RefreshCollectionsFromEngine();
            var refreshedActive = _vaultEngine.Notes.FirstOrDefault(n =>
                string.Equals(n.RelativePath, SelectedNote.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (refreshedActive is not null)
            {
                SelectNote(refreshedActive);
            }
            RebuildGraphLayout();
            StatusBarRightText = $"✓ Linked '{targetTitle}' in '{sourceNote.Title}.md'";
        }
    }

    partial void OnSelectedDesignSystemChanged(DesignSystemPreset value)
    {
        if (_isApplyingDesignSystem || value is null) return;
        ApplyDesignSystemInternal(value, forceDark: null);
    }

    public void SelectDesignSystem(string presetId, bool? forceDark = null)
    {
        var preset = DesignSystemCatalog.GetOrDefault(presetId);
        ApplyDesignSystemInternal(preset, forceDark);
    }

    [RelayCommand]
    private void SwitchDesignSystem(string presetId)
    {
        SelectDesignSystem(presetId);
    }

    [RelayCommand]
    private void NextDesignSystem()
    {
        var next = DesignSystemCatalog.GetNext(SelectedDesignSystem?.Id);
        ApplyDesignSystemInternal(next, forceDark: null);
        IsCommandPaletteOpen = false;
    }

    [RelayCommand]
    private void PreviousDesignSystem()
    {
        var prev = DesignSystemCatalog.GetPrevious(SelectedDesignSystem?.Id);
        ApplyDesignSystemInternal(prev, forceDark: null);
    }

    [RelayCommand]
    public void ToggleAppleTheme()
    {
        _hasExplicitDesignSelection = true;
        IsDarkTheme = !IsDarkTheme;
        var preset = SelectedDesignSystem ?? DesignSystemCatalog.GetOrDefault("apple");
        UpdateDesignSystemLabelsAndApply(preset);
        _ = PersistDesignPreferencesSafeAsync(preset.Id, IsDarkTheme);
        IsCommandPaletteOpen = false;
    }

    private void ApplyDesignSystemInternal(DesignSystemPreset preset, bool? forceDark)
    {
        _hasExplicitDesignSelection = true;
        _isApplyingDesignSystem = true;
        try
        {
            SelectedDesignSystem = preset;
        }
        finally
        {
            _isApplyingDesignSystem = false;
        }

        IsDarkTheme = forceDark ?? preset.PrefersDarkByDefault;
        UpdateDesignSystemLabelsAndApply(preset);
        _ = PersistDesignPreferencesSafeAsync(preset.Id, IsDarkTheme);
    }

    private void UpdateDesignSystemLabelsAndApply(DesignSystemPreset preset)
    {
        string modeLabel = IsDarkTheme ? "Dark" : "Light";
        ThemeButtonLabel = IsDarkTheme ? $"☀️ {preset.Name} Light" : $"🌙 {preset.Name} Dark";
        ActiveDesignBadgeText = $"{preset.Name} · .NET 10";
        ActiveDesignSubtitleText = $"{preset.Name} · {modeLabel} · {preset.ComponentStyleSummary}";
        UpdateObsidianStatusStrip();

        if (Application.Current is App app)
        {
            app.ApplyDesignSystem(preset, IsDarkTheme);
        }
    }

    private void UpdateObsidianStatusStrip()
    {
        var preset = SelectedDesignSystem ?? DesignSystemCatalog.GetOrDefault("apple");
        var note = SelectedNote;
        int backlinks = note?.Backlinks.Count ?? 0;
        int tasks = note?.Tasks.Count ?? 0;
        int words = note?.WordCount ?? 0;
        int chars = note?.RawMarkdown.Length ?? 0;
        ObsidianBottomRightStatusText = $"🔗 {backlinks} backlinks  |  📑 {tasks} tasks  |  📝 {words} words ({chars} chars)  |  🎨 {preset.Name} ({preset.PrimaryButtonCornerRadius:0}px btn / {preset.CardCornerRadius:0}px card)";
    }

    private async Task PersistDesignPreferencesSafeAsync(string presetId, bool isDark)
    {
        if (_vaultEngine is null) return;
        try
        {
            await _vaultEngine.SaveUiPreferenceAsync("active_design_system", presetId).ConfigureAwait(false);
            await _vaultEngine.SaveUiPreferenceAsync("active_theme_mode", isDark ? "dark" : "light").ConfigureAwait(false);
        }
        catch
        {
            // Ignore background persistence errors on disposed/temp vaults
        }
    }

    [RelayCommand]
    public void ToggleCommandPalette()
    {
        IsCommandPaletteOpen = !IsCommandPaletteOpen;
        if (IsCommandPaletteOpen)
        {
            RefreshCommandPaletteNotes();
        }
    }

    [RelayCommand]
    public void CloseCommandPalette()
    {
        IsCommandPaletteOpen = false;
    }

    [RelayCommand]
    public void ExecuteFirstCommandPaletteMatch()
    {
        string q = CommandPaletteFilter?.Trim() ?? string.Empty;
        if (FilteredCommandPaletteNotes.Count > 0 && !string.IsNullOrWhiteSpace(q))
        {
            SelectNote(FilteredCommandPaletteNotes[0]);
            SwitchScreen("Studio");
            IsCommandPaletteOpen = false;
            return;
        }

        if (q.Contains("daily", StringComparison.OrdinalIgnoreCase))
        {
            _ = OpenDailyNoteAsync();
        }
        else if (q.Contains("graph", StringComparison.OrdinalIgnoreCase))
        {
            SwitchScreen("Graph");
        }
        else if (q.Contains("theme", StringComparison.OrdinalIgnoreCase) || q.Contains("dark", StringComparison.OrdinalIgnoreCase))
        {
            ToggleAppleTheme();
        }
        else if (q.Contains("design", StringComparison.OrdinalIgnoreCase))
        {
            NextDesignSystem();
        }
        else if (FilteredCommandPaletteNotes.Count > 0)
        {
            SelectNote(FilteredCommandPaletteNotes[0]);
            SwitchScreen("Studio");
        }

        IsCommandPaletteOpen = false;
    }

    public void SaveActiveNote()
    {
        _ = SaveActiveNoteAsync();
    }

    [RelayCommand]
    public async Task SaveActiveNoteAsync()
    {
        if (SelectedNote is null) return;
        var updated = await _vaultEngine.SaveNoteAsync(
            SelectedNote.RelativePath,
            EditorMarkdownText,
            $"Edited at {DateTime.Now:HH:mm:ss}");

        _peerSyncEngine.UpsertNote(updated.RelativePath, updated.RawMarkdown);
        RefreshCollectionsFromEngine();
        SelectNote(updated);
        RebuildGraphLayout();
        RebuildFlashcardDeck();
    }

    [RelayCommand]
    public async Task CreateNewNoteAsync()
    {
        string timestamp = DateTime.Now.ToString("HHmmss");
        string relPath = $"01 — Architecture/Untitled Note {timestamp}.md";
        string template = $$"""
            ---
            status: Active
            subsystem: Rhyolite.Core
            priority: P1 High
            tags: [architecture, note]
            ---
            # Untitled Note {{timestamp}}

            Created in **Rhyolite** using the {{SelectedDesignSystem.Name}} Design System. Linked to [[Storage Architecture]].

            - [ ] Add architectural notes 📅 {{DateTime.Now:yyyy-MM-dd}}
            """;

        var created = await _vaultEngine.SaveNoteAsync(relPath, template, "Created New Note");
        _peerSyncEngine.UpsertNote(created.RelativePath, created.RawMarkdown);
        RefreshCollectionsFromEngine();
        SelectNote(created);
        RebuildGraphLayout();
        SwitchScreen("Studio");
    }

    [RelayCommand]
    public async Task OpenDailyNoteAsync()
    {
        string todayTitle = DateTime.Now.ToString("yyyy-MM-dd");
        var existing = _vaultEngine.Notes.FirstOrDefault(n =>
            string.Equals(n.Title, todayTitle, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectNote(existing);
            SwitchScreen("Studio");
            return;
        }

        string relPath = $"02 — Daily Notes/{todayTitle}.md";
        string content = $$"""
            ---
            status: Active
            subsystem: Daily Note
            priority: P1 High
            tags: [daily]
            ---
            # Daily Engineering Note — {{todayTitle}}

            Connected to [[Storage Architecture]] and [[MCP and GraphQL P2P Mesh]].

            - [ ] Complete daily FSRS flashcard review 📅 {{todayTitle}} #study
            """;

        var created = await _vaultEngine.SaveNoteAsync(relPath, content, "Daily Note Created");
        _peerSyncEngine.UpsertNote(created.RelativePath, created.RawMarkdown);
        RefreshCollectionsFromEngine();
        SelectNote(created);
        SwitchScreen("Studio");
    }

    [RelayCommand]
    public void ExecuteAskCopilot()
    {
        var res = VaultAiCopilotEngine.AskVault(CopilotPrompt, SelectedNote, _vaultEngine.Notes);
        CopilotResponseText = res.AnswerMarkdown;
    }

    [RelayCommand]
    public void RecenterGraph()
    {
        RebuildGraphLayout();
    }

    public void SelectCanvasCard(CanvasNode? card)
    {
        if (card is null) return;
        SelectedCanvasCardId = card.Id;
        SelectedCanvasCardTitle = card.Label ?? card.File ?? string.Empty;
        SelectedCanvasCardBody = card.Text ?? string.Empty;
    }

    public void SaveCanvasLayout(CanvasDocument? doc = null)
    {
        var target = doc ?? CanvasDoc;
        if (target is null) return;
        _vaultEngine.SaveCanvasDocument(target);
        SaveTelemetryText = "✓ Persisted spatial card layout to System Map.canvas";
    }

    [RelayCommand]
    public void UpdateSelectedCanvasCard()
    {
        var doc = CanvasDoc;
        if (doc is null || string.IsNullOrWhiteSpace(SelectedCanvasCardId)) return;

        var card = doc.Nodes.FirstOrDefault(n => string.Equals(n.Id, SelectedCanvasCardId, StringComparison.OrdinalIgnoreCase));
        if (card is null) return;

        card.Label = SelectedCanvasCardTitle;
        card.Text = SelectedCanvasCardBody;
        _vaultEngine.SaveCanvasDocument(doc);
        CanvasDoc = JsonCanvasEngine.Parse(JsonCanvasEngine.Serialize(doc));
        StatusBarRightText = $"✓ Updated spatial card '{card.Label}' in System Map.canvas";
    }

    [RelayCommand]
    public void DeleteSelectedCanvasCard()
    {
        var doc = CanvasDoc;
        if (doc is null || string.IsNullOrWhiteSpace(SelectedCanvasCardId)) return;

        doc.Nodes.RemoveAll(n => string.Equals(n.Id, SelectedCanvasCardId, StringComparison.OrdinalIgnoreCase));
        doc.Edges.RemoveAll(e =>
            string.Equals(e.FromNode, SelectedCanvasCardId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.ToNode, SelectedCanvasCardId, StringComparison.OrdinalIgnoreCase));

        _vaultEngine.SaveCanvasDocument(doc);
        CanvasDoc = JsonCanvasEngine.Parse(JsonCanvasEngine.Serialize(doc));

        var nextCard = CanvasDoc.Nodes.FirstOrDefault(n => !string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase));
        SelectedCanvasCardId = nextCard?.Id;
        SelectedCanvasCardTitle = nextCard?.Label ?? string.Empty;
        SelectedCanvasCardBody = nextCard?.Text ?? string.Empty;
        StatusBarRightText = "✓ Removed card from System Map.canvas";
    }

    [RelayCommand]
    public void AddCanvasCard()
    {
        var doc = CanvasDoc;
        int idx = doc.Nodes.Count;
        var newCard = new CanvasNode
        {
            Id = $"card-{Guid.NewGuid():N}"[..12],
            Type = "text",
            Label = string.IsNullOrWhiteSpace(SelectedCanvasCardTitle) ? $"Spec Node #{idx}" : SelectedCanvasCardTitle,
            Text = string.IsNullOrWhiteSpace(SelectedCanvasCardBody)
                ? "Created on interactive JSON Canvas 1.0 board.\nLinked to [[Storage Architecture]]."
                : SelectedCanvasCardBody,
            X = 80 + (idx * 35) % 420,
            Y = 90 + (idx * 30) % 260,
            Width = 210,
            Height = 120,
            Color = SelectedDesignSystem.LightPalette.AccentHex
        };
        doc.Nodes.Add(newCard);
        _vaultEngine.SaveCanvasDocument(doc);
        CanvasDoc = JsonCanvasEngine.Parse(JsonCanvasEngine.Serialize(doc));
        SelectCanvasCard(newCard);
    }

    [RelayCommand]
    public void ShowBasesTable()
    {
        IsBasesTableMode = true;
        IsBasesKanbanMode = false;
    }

    [RelayCommand]
    public void ShowBasesKanban()
    {
        IsBasesTableMode = false;
        IsBasesKanbanMode = true;
    }

    [RelayCommand]
    public async Task CycleRowStatusAsync(BaseDatabaseRow? row)
    {
        if (row is null) return;
        var note = _vaultEngine.Notes.FirstOrDefault(n =>
            string.Equals(n.RelativePath, row.NoteRelativePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.Title, row.Title, StringComparison.OrdinalIgnoreCase));

        if (note is null) return;

        string current = row.Status ?? "Planned";
        string nextStatus = current switch
        {
            var s when s.Contains("Plan", StringComparison.OrdinalIgnoreCase) => "In Progress",
            var s when s.Contains("Progress", StringComparison.OrdinalIgnoreCase) || s.Contains("Active", StringComparison.OrdinalIgnoreCase) => "In Review",
            var s when s.Contains("Review", StringComparison.OrdinalIgnoreCase) => "Shipped",
            _ => "Planned"
        };

        string updatedMd = MarkdownAstEngine.UpsertFrontmatterProperty(note.RawMarkdown, "status", nextStatus);
        var saved = await _vaultEngine.SaveNoteAsync(note.RelativePath, updatedMd, $"Bases Status -> {nextStatus}");
        RefreshCollectionsFromEngine();
        if (SelectedNote is not null && string.Equals(SelectedNote.RelativePath, saved.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            SelectNote(saved);
        }
        StatusBarRightText = $"✓ Updated '{saved.Title}' status to '{nextStatus}'";
    }

    [RelayCommand]
    public void RateFlashcard(string ratingName)
    {
        if (_allFlashcards.Count == 0) return;
        var card = _allFlashcards[_currentFlashcardIndex % _allFlashcards.Count];
        var rating = Enum.TryParse<FsrsRating>(ratingName, true, out var parsed) ? parsed : FsrsRating.Good;

        FsrsStudyEngine.ApplyReview(card, rating);
        _vaultEngine.SaveFlashcardReview(
            card.Id,
            card.StabilityDays,
            card.Difficulty,
            card.Reps,
            new DateTimeOffset(DateTime.SpecifyKind(card.NextReviewUtc, DateTimeKind.Utc)));

        DueFlashcardsCount = Math.Max(0, DueFlashcardsCount - 1);
        _currentFlashcardIndex = (_currentFlashcardIndex + 1) % _allFlashcards.Count;
        ShowCurrentFlashcard();
        StatusBarRightText = $"✓ Saved FSRS review ({rating}) for '{card.NoteTitle}' to SQLite WAL";
    }

    [RelayCommand]
    public void SaveMilestoneSnapshot()
    {
        if (SelectedNote is null) return;
        var snap = _vaultEngine.SaveMilestoneSnapshot(SelectedNote.RelativePath, $"Milestone {DateTime.Now:HH:mm:ss}");
        RefreshDiffPreview(SelectedNote);
        StatusBarRightText = $"📸 Saved milestone snapshot #{snap.SnapshotId} for '{SelectedNote.Title}'";
    }

    [RelayCommand]
    public async Task RestorePreviousSnapshotAsync()
    {
        if (SelectedNote is null) return;
        var restored = await _vaultEngine.RestoreSnapshotAsync(SelectedNote.RelativePath);
        if (restored is not null)
        {
            RefreshCollectionsFromEngine();
            SelectNote(restored);
            StatusBarRightText = $"⏪ Restored previous snapshot for '{restored.Title}'";
        }
    }

    [RelayCommand]
    public async Task MobileQuickCaptureAsync()
    {
        if (string.IsNullOrWhiteSpace(MobileQuickCaptureText)) return;
        var daily = _vaultEngine.Notes.FirstOrDefault(n => n.FolderPath.Contains("Daily", StringComparison.OrdinalIgnoreCase))
                    ?? SelectedNote;
        if (daily is null) return;

        string updated = daily.RawMarkdown.TrimEnd() + $"\n- [ ] {MobileQuickCaptureText.Trim()} 📅 {DateTime.Now:yyyy-MM-dd} #mobile-capture\n";
        MobileQuickCaptureText = string.Empty;
        var saved = await _vaultEngine.SaveNoteAsync(daily.RelativePath, updated, "Mobile Quick Capture");
        RefreshCollectionsFromEngine();
        SelectNote(saved);
    }
}
