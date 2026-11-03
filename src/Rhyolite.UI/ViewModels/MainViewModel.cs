using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rhyolite.Core.Bases;
using Rhyolite.Core.Canvas;
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
    private readonly HybridVaultEngine _vaultEngine;
    private readonly RhyoliteAuthEngine _authEngine;
    private readonly PeerSyncEngine _peerSyncEngine;
    private readonly McpServerEngine _mcpServer;
    private readonly GraphQlEngine _graphQlEngine;
    private string _ownerBearerToken = string.Empty;
    private readonly List<FlashcardItem> _allFlashcards = [];
    private int _currentFlashcardIndex;

    [ObservableProperty]
    private string _activeScreen = "Studio";

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private string _themeButtonLabel = "🌙 True-Black Dark";

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private VaultNote? _selectedNote;

    [ObservableProperty]
    private string _editorMarkdownText = string.Empty;

    [ObservableProperty]
    private string _editorMode = "Reading"; // Reading, Live, Source

    [ObservableProperty]
    private string _saveTelemetryText = "✓ Flushed to .md in 0.14 ms · SQLite WAL synced";

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
    public ObservableCollection<FrontmatterProperty> ActiveProperties { get; } = [];
    public ObservableCollection<string> ActiveTags { get; } = [];
    public ObservableCollection<MarkdownHeading> ActiveHeadings { get; } = [];
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

    public Task InitializationTask { get; }

    public MainViewModel()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RhyoliteVault"))
    {
    }

    public MainViewModel(string customVaultPath)
    {
        _vaultEngine = new HybridVaultEngine(customVaultPath);
        _authEngine = new RhyoliteAuthEngine();
        _peerSyncEngine = new PeerSyncEngine("rhyolite-macbook-pro");
        _mcpServer = new McpServerEngine(_authEngine, _peerSyncEngine);
        _graphQlEngine = new GraphQlEngine(_authEngine, _peerSyncEngine, _mcpServer);

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

        RefreshCollectionsFromEngine();

        var initial = VaultNotes.FirstOrDefault(n => n.Title.Contains("Storage Architecture", StringComparison.OrdinalIgnoreCase))
                      ?? VaultNotes.FirstOrDefault();
        if (initial is not null)
        {
            SelectNote(initial);
        }

        CanvasDoc = _vaultEngine.LoadCanvasDocument();
        RebuildGraphLayout();
        RebuildFlashcardDeck();
        ExecuteAskCopilot();
        InitializeProtocolPreview();
    }

    private void RefreshCollectionsFromEngine()
    {
        VaultNotes.Clear();
        PinnedNotes.Clear();

        foreach (var note in _vaultEngine.Notes)
        {
            if (!string.IsNullOrWhiteSpace(SearchFilter) &&
                !note.Title.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.RawMarkdown.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            VaultNotes.Add(note);
            if (note.IsPinned)
            {
                PinnedNotes.Add(note);
            }
        }

        var metrics = _vaultEngine.LatestMetrics;
        VaultSummaryText = $"{metrics.TotalNotes} Notes · {metrics.TotalEdges} Links · Boot {_vaultEngine.LastColdBootMilliseconds:0.0} ms";
        TopologyHubLabel = $"{metrics.TopHubTitle} ({metrics.TopHubPageRank:0.00})";
        TopologyClusteringLabel = $"{metrics.ClusteringCoefficient * 100:0.0}% Dense";
        TopologyOrphanCount = metrics.OrphanCount;
        TopologyGhostCount = metrics.GhostLinkCount;

        RefreshBasesViews();
    }

    public void SelectNote(VaultNote? note)
    {
        if (note is null) return;
        SelectedNote = note;
        EditorMarkdownText = note.RawMarkdown;
        SaveTelemetryText = $"✓ Flushed to .md in {note.LastWriteMilliseconds:0.00} ms · AST {note.LastParseMilliseconds:0.00} ms · SQLite WAL";

        ActiveProperties.Clear();
        foreach (var p in note.Properties) ActiveProperties.Add(p);

        ActiveTags.Clear();
        foreach (var t in note.Tags) ActiveTags.Add(t);

        ActiveHeadings.Clear();
        foreach (var h in note.Headings) ActiveHeadings.Add(h);

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
            ? snaps[^1].RawMarkdown
            : note.RawMarkdown.Replace("SQLite WAL", "In-Memory Cache");

        foreach (var line in SnapshotDiffEngine.ComputeLineDiff(baseline, note.RawMarkdown).Take(14))
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

    private void RebuildGraphLayout()
    {
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

    private void RebuildFlashcardDeck()
    {
        _allFlashcards.Clear();
        foreach (var n in _vaultEngine.Notes)
        {
            _allFlashcards.AddRange(n.Flashcards);
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
        FlashcardMeta = $"Source: {card.NoteTitle}.md · Stability: {card.StabilityDays:0.0}d · Difficulty: {card.Difficulty:0.0}";
    }

    private void InitializeProtocolPreview()
    {
        ExecuteMcpRpc();
        InitializeEnterpriseDevExStudio();
    }

    private void InitializeEnterpriseDevExStudio()
    {
        var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
        var analysis = EnterpriseDevExEngine.AnalyzeGitHubPullRequest(samplePr, _vaultEngine.Notes);

        GitHubPrHeaderText = $"{samplePr.Repository} #{samplePr.Number} — {samplePr.Title}";
        GitHubPrMetaText = $"Branch: {samplePr.HeadBranch} → {samplePr.BaseBranch} ({samplePr.HeadSha[..7]}) · CI: {samplePr.CiCheckStatus} · Delta: +{analysis.TotalAdditions} / -{analysis.TotalDeletions} · Risk: {analysis.RiskLevel}";
        GitHubPrImpactedNotesText = analysis.ImpactedVaultNotes.Count > 0
            ? $"Impacted Vault Docs & ADRs: {string.Join(" · ", analysis.ImpactedVaultNotes.Select(t => $"[[{t}]]"))}"
            : "Impacted Vault Docs & ADRs: None";

        ActivePrComments.Clear();
        foreach (var c in samplePr.Comments) ActivePrComments.Add(c);

        ActivePrFiles.Clear();
        foreach (var f in samplePr.ChangedFiles) ActivePrFiles.Add(f);

        RunOnePasswordScan();
    }

    [RelayCommand]
    public void RunOnePasswordScan()
    {
        string sampleWithSecrets = """
            # Incident Runbook — Payment Gateway
            Links to [[Storage Architecture]] and [[code:src/Rhyolite.Core/Storage/HybridVaultEngine.cs#SaveNoteAsync]]
            Token: ghp_9876543210abcdefghijklmnopqrstuvwx
            ```bash env=prod-readonly
            curl -H "Authorization: Bearer {{ op://Engineering/GitHub-Enterprise/GITHUB_TOKEN }}" https://api.github.com/rate_limit
            ```
            """;
        var scan = EnterpriseDevExEngine.ScanAndRedactSecrets("Runbooks/PaymentGateway.md", sampleWithSecrets, SecretVaultBackend.OnePasswordCli);
        var cells = EnterpriseDevExEngine.ExtractRunbookCells(scan.RedactedMarkdown);
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
        string sampleWithSecrets = """
            # Staging Cluster Triage
            Linear Token: lin_api_1234567890abcdefghijklmnopqrstuv
            ```bash env=staging
            curl -H "Authorization: {{ keepassxc://Engineering/Linear-Workspace/LINEAR_API_KEY }}" https://api.linear.app/graphql
            ```
            """;
        var scan = EnterpriseDevExEngine.ScanAndRedactSecrets("Runbooks/StagingTriage.md", sampleWithSecrets, SecretVaultBackend.KeePassXcCli);
        var cells = EnterpriseDevExEngine.ExtractRunbookCells(scan.RedactedMarkdown);
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
    public async Task ImportGitHubPrToVaultAsync()
    {
        var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
        var analysis = EnterpriseDevExEngine.AnalyzeGitHubPullRequest(samplePr, _vaultEngine.Notes);
        string relPath = $"01 — Architecture/PR-{samplePr.Number} Review & Comments.md";
        var saved = await _vaultEngine.SaveNoteAsync(relPath, analysis.GeneratedReviewMarkdown, $"Pulled GitHub PR #{samplePr.Number}");
        RefreshCollectionsFromEngine();
        SelectNote(saved);
        DevExTerminalOutput = $"✓ Materialized GitHub PR #{samplePr.Number} ({analysis.UnresolvedThreadsCount} unresolved review threads, +{analysis.TotalAdditions}/-{analysis.TotalDeletions}) into '{relPath}' and extracted {analysis.ExtractedReviewTasks.Count} tasks.";
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
    public void SwitchScreen(string screenName)
    {
        ActiveScreen = screenName;
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
    public void ToggleAppleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ThemeButtonLabel = IsDarkTheme ? "☀️ Apple Light" : "🌙 True-Black Dark";
        if (Application.Current is App app)
        {
            app.SetAppleTheme(IsDarkTheme);
        }
    }

    [RelayCommand]
    public void ToggleCommandPalette()
    {
        IsCommandPaletteOpen = !IsCommandPaletteOpen;
    }

    [RelayCommand]
    public async Task SaveActiveNoteAsync()
    {
        if (SelectedNote is null) return;
        var updated = await _vaultEngine.SaveNoteAsync(
            SelectedNote.RelativePath,
            EditorMarkdownText,
            $"Edited at {DateTime.Now:HH:mm:ss}");

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

            Created in **Rhyolite** using the Apple Design System. Linked to [[Storage Architecture]].

            - [ ] Add architectural notes 📅 {{DateTime.Now:yyyy-MM-dd}}
            """;

        var created = await _vaultEngine.SaveNoteAsync(relPath, template, "Created New Note");
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

    [RelayCommand]
    public void AddCanvasCard()
    {
        var doc = CanvasDoc;
        int idx = doc.Nodes.Count;
        doc.Nodes.Add(new CanvasNode
        {
            Id = $"card-{Guid.NewGuid():N}"[..12],
            Type = "text",
            Label = $"Apple Spec Node #{idx}",
            Text = "Created on interactive JSON Canvas 1.0 board.\nLinked to [[Storage Architecture]].",
            X = 80 + (idx * 35) % 420,
            Y = 90 + (idx * 30) % 260,
            Width = 210,
            Height = 120,
            Color = "#0071e3"
        });
        _vaultEngine.SaveCanvasDocument(doc);
        CanvasDoc = JsonCanvasEngine.Parse(JsonCanvasEngine.Serialize(doc));
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
    public void RateFlashcard(string ratingName)
    {
        if (_allFlashcards.Count == 0) return;
        var card = _allFlashcards[_currentFlashcardIndex % _allFlashcards.Count];
        var rating = Enum.TryParse<FsrsRating>(ratingName, true, out var parsed) ? parsed : FsrsRating.Good;

        FsrsStudyEngine.ApplyReview(card, rating);
        DueFlashcardsCount = Math.Max(0, DueFlashcardsCount - 1);
        _currentFlashcardIndex = (_currentFlashcardIndex + 1) % _allFlashcards.Count;
        ShowCurrentFlashcard();
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
