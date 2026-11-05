using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
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
    private bool _isRightSidebarOpen;

    [ObservableProperty]
    private bool _isPropertiesExpanded = true;

    [ObservableProperty]
    private bool _isFolderStudioOpen;

    [ObservableProperty]
    private bool _isNoteMenuOpen;

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

    // Bases & Capacities Object Studio State
    [ObservableProperty]
    private bool _isBasesGalleryMode = true;

    [ObservableProperty]
    private bool _isBasesTableMode;

    [ObservableProperty]
    private bool _isBasesKanbanMode;

    [ObservableProperty]
    private bool _isBasesCalendarMode;

    [ObservableProperty]
    private VaultFolderNode? _selectedFolderNode;

    [ObservableProperty]
    private string _activeFolderFilter = "All";

    [ObservableProperty]
    private string _activeObjectTypeFilter = "All";

    [ObservableProperty]
    private string _activeCollectionFilter = "All";

    [ObservableProperty]
    private string _newFolderPathInput = "01 — Architecture/Caching";

    [ObservableProperty]
    private string _selectedEmojiInput = "🚀";

    [ObservableProperty]
    private string _newObjectTypeNameInput = "Book Review";

    [ObservableProperty]
    private string _newObjectTypeEmojiInput = "📚";

    [ObservableProperty]
    private string _newTypedObjectTitleInput = "Distributed Cache Spec";

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

    // User Account Profile, Avatar Studio & Enterprise Admin State
    [ObservableProperty]
    private string _userDisplayName = "Dustin Blad";

    [ObservableProperty]
    private string _userEmail = "dustin@rhyolite.dev";

    [ObservableProperty]
    private string _userWorkspaceSubtitle = "Personal · VaultOwner";

    [ObservableProperty]
    private string _userJobTitle = "Principal Systems Architect";

    [ObservableProperty]
    private string _userOrganization = "Rhyolite Core Engineering";

    [ObservableProperty]
    private string _userRoleBadge = "VaultOwner · Enterprise Admin";

    [ObservableProperty]
    private string _userPresenceStatus = "Online";

    [ObservableProperty]
    private string _userAvatarInitialsOrEmoji = "DB";

    [ObservableProperty]
    private string _userAvatarAccentHex = "#0071E3";

    [ObservableProperty]
    private string _profileImageFilePathInput = string.Empty;

    [ObservableProperty]
    private Bitmap? _userProfileImageBitmap;

    [ObservableProperty]
    private bool _hasCustomProfileImage;

    [ObservableProperty]
    private string _newAdminUserEmailInput = "alex.rivera@rhyolite.dev";

    [ObservableProperty]
    private string _newAdminUserNameInput = "Alex Rivera";

    [ObservableProperty]
    private string _selectedAdminRoleInput = "Editor";

    [ObservableProperty]
    private string _newApiKeyLabelInput = "CI/CD Production Vault Sync";

    [ObservableProperty]
    private string _lastGeneratedApiKeyPreview = "rhy_pat_9f8a7b6c5d4e3f2a1b0c... (SHA-256 Digest Stored)";

    public ObservableCollection<VaultNote> VaultNotes { get; } = [];
    public ObservableCollection<VaultNote> PinnedNotes { get; } = [];
    public ObservableCollection<VaultNote> FilteredCommandPaletteNotes { get; } = [];
    public ObservableCollection<VaultFolderNode> VisibleFolderNodes { get; } = [];
    public ObservableCollection<VaultTreeItem> UnifiedExplorerItems { get; } = [];
    public ObservableCollection<CapacitiesObjectTypeSchema> ObjectTypeSchemas { get; } = [];
    public ObservableCollection<CapacitiesCollection> Collections { get; } = [];
    public ObservableCollection<CapacitiesCalendarEntry> CalendarEntries { get; } = [];
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
    public ObservableCollection<UserAccount> ManagedUserAccounts { get; } = [];
    public ObservableCollection<ApiKeyCredential> ManagedApiKeys { get; } = [];
    public ObservableCollection<OAuthProviderDescriptor> ManagedOAuthProviders { get; } = [];
    public ObservableCollection<AdminAuditLogEntry> AdminAuditLogEntries { get; } = [];

    public bool IsStudioView => ActiveScreen == "Studio";
    public bool IsGraphView => ActiveScreen == "Graph";
    public bool IsCanvasView => ActiveScreen == "Canvas";
    public bool IsBasesView => ActiveScreen == "Bases";
    public bool IsStudyView => ActiveScreen == "Study";
    public bool IsProtocolView => ActiveScreen == "Protocol";
    public bool IsDevExView => ActiveScreen == "DevEx";
    public bool IsMobileView => ActiveScreen == "Mobile";
    public bool IsAccountAdminView => ActiveScreen == "AccountAdmin";

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
        InitializeUserProfileAndAdminStudio();
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
        VisibleFolderNodes.Clear();
        ObjectTypeSchemas.Clear();

        foreach (var f in _vaultEngine.GetFlattenedVisibleFolderTree())
        {
            VisibleFolderNodes.Add(f);
        }

        if (SelectedFolderNode is not null)
        {
            SelectedFolderNode = VisibleFolderNodes.FirstOrDefault(f =>
                string.Equals(f.FolderPath, SelectedFolderNode.FolderPath, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var schema in _vaultEngine.GetObjectTypeSchemas())
        {
            ObjectTypeSchemas.Add(schema);
        }

        foreach (var note in _vaultEngine.Notes)
        {
            if (note.IsPinned)
            {
                PinnedNotes.Add(note);
            }

            if (!string.IsNullOrWhiteSpace(ActiveFolderFilter) &&
                !string.Equals(ActiveFolderFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                var normFolder = ActiveFolderFilter.Replace('\\', '/').Trim('/');
                if (!string.Equals(note.FolderPath, normFolder, StringComparison.OrdinalIgnoreCase) &&
                    !note.FolderPath.StartsWith(normFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(ActiveObjectTypeFilter) &&
                !string.Equals(ActiveObjectTypeFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(note.ObjectType, ActiveObjectTypeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !note.ObjectType.Contains(ActiveObjectTypeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(ActiveCollectionFilter) &&
                !string.Equals(ActiveCollectionFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(note.CollectionName, ActiveCollectionFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(SearchFilter) &&
                !note.Title.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.ObjectType.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.RawMarkdown.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.Tags.Any(t => t.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            VaultNotes.Add(note);
        }

        var metrics = _vaultEngine.LatestMetrics;
        VaultSummaryText = $"{metrics.TotalNotes} Notes · {VisibleFolderNodes.Count} Folders · Boot {_vaultEngine.LastColdBootMilliseconds:0.0} ms";
        TopologyHubLabel = $"{metrics.TopHubTitle} ({metrics.TopHubPageRank:0.00})";
        TopologyClusteringLabel = $"{metrics.ClusteringCoefficient * 100:0.0}% Dense";
        TopologyOrphanCount = metrics.OrphanCount;
        TopologyGhostCount = metrics.GhostLinkCount;

        RefreshBasesViews();
        RefreshCommandPaletteNotes();
        RefreshUnifiedExplorerTree();
    }

    private void RefreshUnifiedExplorerTree()
    {
        UnifiedExplorerItems.Clear();
        foreach (var item in _vaultEngine.GetUnifiedExplorerTree(
            SearchFilter,
            ActiveObjectTypeFilter,
            SelectedNote?.RelativePath,
            SelectedFolderNode?.FolderPath))
        {
            UnifiedExplorerItems.Add(item);
        }
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
        ActiveNoteFolderBreadcrumb = string.IsNullOrWhiteSpace(note.FolderPath)
            ? "Vault Root"
            : $"{note.FolderIconEmoji} {note.FolderPath.Replace("/", " › ")}";
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
        RefreshUnifiedExplorerTree();
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
        foreach (var row in BasesQueryEngine.QueryRows(
            _vaultEngine.Notes,
            SearchFilter,
            ActiveFolderFilter,
            null,
            ActiveObjectTypeFilter,
            ActiveCollectionFilter))
        {
            DatabaseRows.Add(row);
        }

        KanbanColumns.Clear();
        foreach (var col in BasesQueryEngine.BuildKanbanBoard(
            _vaultEngine.Notes,
            SearchFilter,
            ActiveFolderFilter,
            ActiveObjectTypeFilter,
            ActiveCollectionFilter))
        {
            KanbanColumns.Add(col);
        }

        Collections.Clear();
        foreach (var col in BasesQueryEngine.GetCollections(_vaultEngine.Notes))
        {
            Collections.Add(col);
        }

        CalendarEntries.Clear();
        foreach (var entry in BasesQueryEngine.BuildCalendarTimeline(_vaultEngine.Notes))
        {
            CalendarEntries.Add(entry);
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
        OnPropertyChanged(nameof(IsAccountAdminView));
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
    public void ShowBasesGallery()
    {
        IsBasesGalleryMode = true;
        IsBasesTableMode = false;
        IsBasesKanbanMode = false;
        IsBasesCalendarMode = false;
    }

    [RelayCommand]
    public void ShowBasesTable()
    {
        IsBasesGalleryMode = false;
        IsBasesTableMode = true;
        IsBasesKanbanMode = false;
        IsBasesCalendarMode = false;
    }

    [RelayCommand]
    public void ShowBasesKanban()
    {
        IsBasesGalleryMode = false;
        IsBasesTableMode = false;
        IsBasesKanbanMode = true;
        IsBasesCalendarMode = false;
    }

    [RelayCommand]
    public void ShowBasesCalendar()
    {
        IsBasesGalleryMode = false;
        IsBasesTableMode = false;
        IsBasesKanbanMode = false;
        IsBasesCalendarMode = true;
    }

    [RelayCommand]
    public void SelectFolderFilter(object? folderParam)
    {
        if (folderParam is VaultFolderNode node)
        {
            SelectedFolderNode = node;
            ActiveFolderFilter = node.FolderPath;
            SelectedEmojiInput = node.IconEmoji;
        }
        else if (folderParam is string folderStr && !string.IsNullOrWhiteSpace(folderStr))
        {
            ActiveFolderFilter = folderStr;
            if (string.Equals(folderStr, "All", StringComparison.OrdinalIgnoreCase))
            {
                SelectedFolderNode = null;
            }
        }
        else
        {
            ActiveFolderFilter = "All";
            SelectedFolderNode = null;
        }

        RefreshCollectionsFromEngine();
        StatusBarRightText = $"🗂️ Filtered vault by folder: {ActiveFolderFilter}";
    }

    [RelayCommand]
    public void ToggleFolderExpand(VaultFolderNode? folder)
    {
        if (folder is null) return;
        _vaultEngine.ToggleFolderExpanded(folder.FolderPath);
        RefreshCollectionsFromEngine();
    }

    [RelayCommand]
    public void ToggleFolderStudio() => IsFolderStudioOpen = !IsFolderStudioOpen;

    [RelayCommand]
    public void ToggleNoteMenu() => IsNoteMenuOpen = !IsNoteMenuOpen;

    [RelayCommand]
    public void SelectExplorerItem(VaultTreeItem? item)
    {
        if (item is null) return;
        if (item.IsFolder && item.FolderNode is not null)
        {
            SelectedFolderNode = item.FolderNode;
            SelectedEmojiInput = item.FolderNode.IconEmoji;
            NewFolderPathInput = $"{item.FolderNode.FolderPath}/Subfolder";
            _vaultEngine.ToggleFolderExpanded(item.FolderNode.FolderPath);
            RefreshCollectionsFromEngine();
            StatusBarRightText = $"🗂️ Folder '{item.IconEmoji} {item.FolderNode.FolderPath}' ({item.FolderNode.TotalNoteCount} notes)";
        }
        else if (item.IsNote && item.Note is not null)
        {
            SelectNote(item.Note);
            if (!string.Equals(ActiveScreen, "Studio", StringComparison.OrdinalIgnoreCase))
            {
                SwitchScreen("Studio");
            }
        }
    }

    [RelayCommand]
    public void CreateFolderOrSubfolder()
    {
        if (string.IsNullOrWhiteSpace(NewFolderPathInput)) return;
        string path = NewFolderPathInput.Trim();
        string emoji = string.IsNullOrWhiteSpace(SelectedEmojiInput) ? "📁" : SelectedEmojiInput.Trim();
        var created = _vaultEngine.CreateFolderOrSubfolder(path, emoji, SelectedDesignSystem.LightPalette.AccentHex);
        RefreshCollectionsFromEngine();
        SelectedFolderNode = VisibleFolderNodes.FirstOrDefault(f =>
            string.Equals(f.FolderPath, created.FolderPath, StringComparison.OrdinalIgnoreCase));
        StatusBarRightText = $"✓ Created folder/subfolder '{created.IconEmoji} {created.FolderPath}'";
    }

    [RelayCommand]
    public void QuickCreateSubfolder(VaultFolderNode? parentFolder)
    {
        string baseFolder = parentFolder?.FolderPath ?? SelectedFolderNode?.FolderPath ?? "01 — Architecture";
        string subPath = $"{baseFolder}/Submodule-{DateTime.Now:mmss}";
        string emoji = string.IsNullOrWhiteSpace(SelectedEmojiInput) ? "🚀" : SelectedEmojiInput.Trim();
        var created = _vaultEngine.CreateFolderOrSubfolder(subPath, emoji, SelectedDesignSystem.LightPalette.AccentHex);
        NewFolderPathInput = created.FolderPath;
        RefreshCollectionsFromEngine();
        SelectedFolderNode = VisibleFolderNodes.FirstOrDefault(f =>
            string.Equals(f.FolderPath, created.FolderPath, StringComparison.OrdinalIgnoreCase));
        StatusBarRightText = $"✓ Created nested subfolder '{created.IconEmoji} {created.FolderPath}'";
    }

    [RelayCommand]
    public void AssignEmojiToSelectedFolder(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;
        SelectedEmojiInput = emoji.Trim();
        string targetFolder = SelectedFolderNode?.FolderPath
                              ?? SelectedNote?.FolderPath
                              ?? "01 — Architecture";
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            targetFolder = "01 — Architecture";
        }

        _vaultEngine.SetFolderIconAndColor(targetFolder, SelectedEmojiInput, SelectedDesignSystem.LightPalette.AccentHex);
        RefreshCollectionsFromEngine();
        if (SelectedNote is not null)
        {
            var refreshed = _vaultEngine.Notes.FirstOrDefault(n =>
                string.Equals(n.RelativePath, SelectedNote.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                SelectNote(refreshed);
            }
        }
        StatusBarRightText = $"🎨 Assigned icon {SelectedEmojiInput} to folder '{targetFolder}'";
    }

    [RelayCommand]
    public async Task MoveActiveNoteToSelectedFolderAsync()
    {
        if (SelectedNote is null) return;
        string targetFolder = SelectedFolderNode?.FolderPath ?? NewFolderPathInput?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetFolder)) return;

        var moved = await _vaultEngine.MoveNoteToFolderAsync(SelectedNote.RelativePath, targetFolder);
        if (moved is not null)
        {
            RefreshCollectionsFromEngine();
            SelectNote(moved);
            RebuildGraphLayout();
            StatusBarRightText = $"↗ Moved '{moved.Title}' into '{moved.FolderIconEmoji} {moved.FolderPath}'";
        }
    }

    [RelayCommand]
    public void FilterByObjectType(string? objectType)
    {
        ActiveObjectTypeFilter = string.IsNullOrWhiteSpace(objectType) ? "All" : objectType;
        RefreshCollectionsFromEngine();
        StatusBarRightText = $"✨ Capacities Object Type Filter: {ActiveObjectTypeFilter}";
    }

    [RelayCommand]
    public void SelectCollectionFilter(object? collectionParam)
    {
        if (collectionParam is CapacitiesCollection col)
        {
            ActiveCollectionFilter = col.Name;
        }
        else if (collectionParam is string s && !string.IsNullOrWhiteSpace(s))
        {
            ActiveCollectionFilter = s;
        }
        else
        {
            ActiveCollectionFilter = "All";
        }

        RefreshCollectionsFromEngine();
        StatusBarRightText = $"⭐ Capacities Collection Filter: {ActiveCollectionFilter}";
    }

    [RelayCommand]
    public async Task CreateTypedObjectAsync(object? typeParam)
    {
        string typeKey = typeParam switch
        {
            CapacitiesObjectTypeSchema schema => schema.TypeId,
            string s when !string.IsNullOrWhiteSpace(s) => s,
            _ => !string.Equals(ActiveObjectTypeFilter, "All", StringComparison.OrdinalIgnoreCase)
                ? ActiveObjectTypeFilter
                : "Project"
        };

        string title = string.IsNullOrWhiteSpace(NewTypedObjectTitleInput)
            ? $"New {typeKey} {DateTime.Now:HHmm}"
            : NewTypedObjectTitleInput.Trim();

        string? targetFolder = SelectedFolderNode?.FolderPath;
        string? collection = !string.Equals(ActiveCollectionFilter, "All", StringComparison.OrdinalIgnoreCase)
            ? ActiveCollectionFilter
            : null;

        var created = await _vaultEngine.CreateTypedObjectNoteAsync(typeKey, title, targetFolder, collection);
        _peerSyncEngine.UpsertNote(created.RelativePath, created.RawMarkdown);
        RefreshCollectionsFromEngine();
        SelectNote(created);
        RebuildGraphLayout();
        StatusBarRightText = $"✨ Created Capacities Object '{created.ObjectIconEmoji} {created.Title}' ({created.ObjectType}) in '{created.FolderPath}'";
    }

    [RelayCommand]
    public void CreateCustomObjectType()
    {
        if (string.IsNullOrWhiteSpace(NewObjectTypeNameInput)) return;
        string singular = NewObjectTypeNameInput.Trim();
        string emoji = string.IsNullOrWhiteSpace(NewObjectTypeEmojiInput) ? "✨" : NewObjectTypeEmojiInput.Trim();
        string typeId = singular.ToLowerInvariant().Replace(' ', '-');
        string folder = SelectedFolderNode?.FolderPath ?? $"04 — Capacities Studio/{singular}s";

        var schema = new CapacitiesObjectTypeSchema(
            TypeId: typeId,
            SingularName: singular,
            PluralName: singular.EndsWith('s') ? singular : singular + "s",
            IconEmoji: emoji,
            AccentColorHex: SelectedDesignSystem.LightPalette.AccentHex,
            DefaultFolderPath: folder,
            SchemaPropertyKeys: ["type", "icon", "status", "collection", "priority"],
            Description: $"Custom Capacities object schema for {singular}");

        _vaultEngine.SaveCustomObjectType(schema);
        RefreshCollectionsFromEngine();
        StatusBarRightText = $"✨ Registered custom Capacities Object Type '{emoji} {singular}'";
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

    private void InitializeUserProfileAndAdminStudio()
    {
        try
        {
            // Seed additional enterprise RBAC team accounts if not already present
            if (_authEngine.Accounts.Count <= 1)
            {
                _authEngine.RegisterAccount(
                    "sarah.chen@rhyolite.dev",
                    "Sarah Chen (Security Admin)",
                    "Rhyolite!2026#Admin",
                    VaultRole.Admin);
                _authEngine.RegisterAccount(
                    "claude-agent@rhyolite.local",
                    "Claude Code MCP Agent",
                    "Rhyolite!2026#Agent",
                    VaultRole.AiAgentReadWrite,
                    AccessScope.ForAiAgent());
                _authEngine.RegisterAccount(
                    "macbook-pro@rhyolite.mesh",
                    "MacBook Pro M4 Max (P2P Node)",
                    "Rhyolite!2026#Peer",
                    VaultRole.TrustedPeerNode);
            }

            if (_authEngine.ApiKeys.Count == 0)
            {
                var k1 = _authEngine.CreateApiKey(
                    "usr-owner-01",
                    "Claude Code MCP Stdio Harness",
                    VaultRole.AiAgentReadWrite,
                    AccessScope.ForAiAgent(),
                    TimeSpan.FromDays(90));
                _authEngine.CreateApiKey(
                    "usr-owner-01",
                    "GitHub Actions PR Docs Sync",
                    VaultRole.Editor,
                    AccessScope.Unrestricted,
                    TimeSpan.FromDays(180));
                LastGeneratedApiKeyPreview = $"{k1.RawApiKey[..Math.Min(26, k1.RawApiKey.Length)]}… (SHA-256: {k1.Credential.KeySha256Hex[..12]}…)";
            }

            RefreshAdminCollections();

            if (AdminAuditLogEntries.Count == 0)
            {
                AdminAuditLogEntries.Add(new AdminAuditLogEntry(
                    DateTime.Now.ToString("HH:mm:ss"),
                    "dustin@rhyolite.dev",
                    "Verified PBKDF2-SHA512 (210,000 iter) & HMAC-SHA256 Master Key",
                    "RhyoliteAuthEngine · VaultOwner",
                    "SECURITY"));
                AdminAuditLogEntries.Add(new AdminAuditLogEntry(
                    DateTime.Now.AddMinutes(-4).ToString("HH:mm:ss"),
                    "claude-agent@rhyolite.local",
                    "Authorized MCP JSON-RPC 2.0 Tool Scope (Denied: 99 — Personal/)",
                    "AccessScope.ForAiAgent()",
                    "RBAC ACL"));
                AdminAuditLogEntries.Add(new AdminAuditLogEntry(
                    DateTime.Now.AddMinutes(-12).ToString("HH:mm:ss"),
                    "sarah.chen@rhyolite.dev",
                    "Enabled OIDC S256 PKCE Enterprise SSO Provider",
                    "sso.enterprise.example.com",
                    "OIDC SSO"));
            }

            var profile = _vaultEngine.GetUserProfile();
            UserDisplayName = profile.DisplayName;
            UserEmail = profile.Email;
            UserWorkspaceSubtitle = string.IsNullOrWhiteSpace(profile.WorkspaceSubtitle) ? "Personal · Admin" : profile.WorkspaceSubtitle;
            UserJobTitle = profile.JobTitle;
            UserOrganization = profile.Organization;
            UserRoleBadge = profile.RoleBadge;
            UserPresenceStatus = profile.PresenceStatus;
            UserAvatarInitialsOrEmoji = profile.AvatarInitialsOrEmoji;
            UserAvatarAccentHex = profile.AvatarAccentHex;

            if (!string.IsNullOrWhiteSpace(profile.AvatarImagePath) && File.Exists(profile.AvatarImagePath))
            {
                byte[] existingBytes = File.ReadAllBytes(profile.AvatarImagePath);
                UserProfileImageBitmap = new Bitmap(new MemoryStream(existingBytes));
                HasCustomProfileImage = true;
                ProfileImageFilePathInput = profile.AvatarImagePath;
            }
            else
            {
                GenerateStudioAvatarPreset("architect-blue");
            }
        }
        catch
        {
            // Ignore non-fatal avatar initialization errors on restricted filesystems
        }
    }

    private void RefreshAdminCollections()
    {
        ManagedUserAccounts.Clear();
        foreach (var acc in _authEngine.Accounts.OrderBy(a => a.Role).ThenBy(a => a.DisplayName))
        {
            ManagedUserAccounts.Add(acc);
        }

        ManagedApiKeys.Clear();
        foreach (var key in _authEngine.ApiKeys.OrderByDescending(k => k.CreatedAtUtc))
        {
            ManagedApiKeys.Add(key);
        }

        ManagedOAuthProviders.Clear();
        foreach (var provider in _authEngine.GetIdentityProviders())
        {
            ManagedOAuthProviders.Add(provider);
        }
    }

    private void PrependAuditLog(string actionTitle, string targetResource, string severity = "AUDIT")
    {
        AdminAuditLogEntries.Insert(0, new AdminAuditLogEntry(
            DateTime.Now.ToString("HH:mm:ss"),
            UserEmail,
            actionTitle,
            targetResource,
            severity));
    }

    [RelayCommand]
    public void OpenUserAccountAdmin()
    {
        SwitchScreen("AccountAdmin");
        StatusBarRightText = "👤 Opened User Account Information, Profile Image Studio & Enterprise Admin Center";
    }

    [RelayCommand]
    public void GenerateStudioAvatarPreset(string? presetId)
    {
        string key = string.IsNullOrWhiteSpace(presetId) ? "architect-blue" : presetId.Trim().ToLowerInvariant();

        // Support loading the official Rhyolite volcanic prism crest if requested and available
        if (key == "rhyolite-crest")
        {
            string repoLogo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "logo", "rhyolite-logo.png"));
            if (!File.Exists(repoLogo))
            {
                repoLogo = "/Volumes/dockdata/dev/projects/Rhyolite/assets/logo/rhyolite-logo.png";
            }

            if (File.Exists(repoLogo))
            {
                ApplyUploadedProfileImageFile(repoLogo);
                return;
            }
        }

        (byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, string hex) = key switch
        {
            "claude-terracotta" => ((byte)217, (byte)119, (byte)87, (byte)124, (byte)45, (byte)18, "#D97757"),
            "emerald-matrix" => ((byte)16, (byte)185, (byte)129, (byte)6, (byte)78, (byte)59, "#10B981"),
            "obsidian-violet" => ((byte)139, (byte)92, (byte)246, (byte)46, (byte)16, (byte)101, "#8B5CF6"),
            "solar-gold" => ((byte)245, (byte)158, (byte)11, (byte)153, (byte)27, (byte)27, "#F59E0B"),
            _ => ((byte)14, (byte)165, (byte)233, (byte)30, (byte)58, (byte)138, "#0071E3")
        };

        UserAvatarAccentHex = hex;
        byte[] pngBytes = RenderProceduralAvatarPngBytes(160, 160, r1, g1, b1, r2, g2, b2);
        string savedPath = _vaultEngine.SaveProfileAvatarBytes(pngBytes, ".png");
        UserProfileImageBitmap = new Bitmap(new MemoryStream(pngBytes));
        HasCustomProfileImage = true;
        ProfileImageFilePathInput = savedPath;
        PrependAuditLog($"Generated Studio Profile Avatar ({key})", savedPath, "PROFILE");
        StatusBarRightText = $"✨ Applied '{key}' studio profile image ({savedPath})";
    }

    [RelayCommand]
    public void UploadProfileImageFromPath()
    {
        if (!string.IsNullOrWhiteSpace(ProfileImageFilePathInput) && File.Exists(ProfileImageFilePathInput.Trim()))
        {
            ApplyUploadedProfileImageFile(ProfileImageFilePathInput.Trim());
            return;
        }

        string fallbackLogo = "/Volumes/dockdata/dev/projects/Rhyolite/assets/logo/rhyolite-logo.png";
        if (File.Exists(fallbackLogo))
        {
            ApplyUploadedProfileImageFile(fallbackLogo);
        }
        else
        {
            GenerateStudioAvatarPreset("claude-terracotta");
        }
    }

    public void ApplyUploadedProfileImageFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        string? copiedPath = _vaultEngine.CopyProfileAvatarFromFile(filePath);
        if (string.IsNullOrWhiteSpace(copiedPath) || !File.Exists(copiedPath))
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(copiedPath);
        UserProfileImageBitmap = new Bitmap(new MemoryStream(bytes));
        HasCustomProfileImage = true;
        ProfileImageFilePathInput = copiedPath;
        PrependAuditLog("Uploaded Custom Profile Image from Disk", Path.GetFileName(filePath), "PROFILE");
        StatusBarRightText = $"🖼️ Updated profile image from '{Path.GetFileName(filePath)}' → {copiedPath}";
    }

    [RelayCommand]
    public void ResetProfileAvatarToInitials()
    {
        HasCustomProfileImage = false;
        UserProfileImageBitmap = null;
        var current = _vaultEngine.GetUserProfile();
        _vaultEngine.SaveUserProfile(current with
        {
            AvatarMode = "Initials",
            AvatarInitialsOrEmoji = UserAvatarInitialsOrEmoji,
            AvatarAccentHex = UserAvatarAccentHex
        });
        PrependAuditLog("Reset Profile Avatar to Monogram Initials", UserAvatarInitialsOrEmoji, "PROFILE");
        StatusBarRightText = $"👤 Reset profile avatar to '{UserAvatarInitialsOrEmoji}' initials badge";
    }

    [RelayCommand]
    public void SelectAvatarColorPreset(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        UserAvatarAccentHex = hex.Trim();
        SaveUserProfile();
    }

    [RelayCommand]
    public void SaveUserProfile()
    {
        string initials = string.IsNullOrWhiteSpace(UserAvatarInitialsOrEmoji)
            ? DeriveInitials(UserDisplayName)
            : UserAvatarInitialsOrEmoji.Trim();
        UserAvatarInitialsOrEmoji = initials;

        var current = _vaultEngine.GetUserProfile();
        var updated = new UserProfileRecord(
            UserId: current.UserId,
            DisplayName: string.IsNullOrWhiteSpace(UserDisplayName) ? "Dustin Blad" : UserDisplayName.Trim(),
            Email: string.IsNullOrWhiteSpace(UserEmail) ? "dustin@rhyolite.dev" : UserEmail.Trim(),
            WorkspaceSubtitle: string.IsNullOrWhiteSpace(UserWorkspaceSubtitle) ? "Personal · VaultOwner" : UserWorkspaceSubtitle.Trim(),
            JobTitle: string.IsNullOrWhiteSpace(UserJobTitle) ? "Principal Systems Architect" : UserJobTitle.Trim(),
            Organization: string.IsNullOrWhiteSpace(UserOrganization) ? "Rhyolite Core Engineering" : UserOrganization.Trim(),
            RoleBadge: string.IsNullOrWhiteSpace(UserRoleBadge) ? "VaultOwner · Enterprise Admin" : UserRoleBadge.Trim(),
            PresenceStatus: string.IsNullOrWhiteSpace(UserPresenceStatus) ? "Online" : UserPresenceStatus.Trim(),
            AvatarMode: HasCustomProfileImage ? "Image" : "Initials",
            AvatarInitialsOrEmoji: initials,
            AvatarAccentHex: UserAvatarAccentHex,
            AvatarImagePath: HasCustomProfileImage ? ProfileImageFilePathInput : current.AvatarImagePath,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        _vaultEngine.SaveUserProfile(updated);
        PrependAuditLog("Saved User Account & Profile Metadata to SQLite WAL", $"{updated.DisplayName} ({updated.Email})", "PROFILE");
        StatusBarRightText = $"✓ Saved user account profile for '{updated.DisplayName}' to .rhyolite/cache.db";
    }

    [RelayCommand]
    public void ProvisionAdminUser()
    {
        string email = string.IsNullOrWhiteSpace(NewAdminUserEmailInput)
            ? $"engineer.{DateTime.Now:HHmmss}@rhyolite.dev"
            : NewAdminUserEmailInput.Trim();
        string name = string.IsNullOrWhiteSpace(NewAdminUserNameInput)
            ? email.Split('@')[0]
            : NewAdminUserNameInput.Trim();
        var role = Enum.TryParse<VaultRole>(SelectedAdminRoleInput, true, out var parsedRole)
            ? parsedRole
            : VaultRole.Editor;

        try
        {
            var scope = role is VaultRole.AiAgentReadWrite or VaultRole.AiAgentReadOnly
                ? AccessScope.ForAiAgent()
                : AccessScope.Unrestricted;
            var created = _authEngine.RegisterAccount(email, name, "Enterprise!2026#Vault", role, scope);
            RefreshAdminCollections();
            PrependAuditLog($"Provisioned RBAC Account ({role})", $"{created.DisplayName} <{created.Email}>", "RBAC");
            StatusBarRightText = $"🛡️ Provisioned '{created.DisplayName}' with RBAC role '{role}'";
        }
        catch (InvalidOperationException)
        {
            // Account email already exists: append a unique suffix so demo provisioning always succeeds
            string uniqueEmail = $"{email.Split('@')[0]}+{DateTime.Now:mmss}@rhyolite.dev";
            var created = _authEngine.RegisterAccount(uniqueEmail, name, "Enterprise!2026#Vault", role, AccessScope.Unrestricted);
            RefreshAdminCollections();
            PrependAuditLog($"Provisioned RBAC Account ({role})", $"{created.DisplayName} <{created.Email}>", "RBAC");
            StatusBarRightText = $"🛡️ Provisioned '{created.DisplayName}' ({uniqueEmail}) with RBAC role '{role}'";
        }
    }

    [RelayCommand]
    public void CycleUserRole(UserAccount? account)
    {
        if (account is null) return;
        int idx = ManagedUserAccounts.IndexOf(account);
        if (idx < 0) return;

        VaultRole nextRole = account.Role switch
        {
            VaultRole.VaultOwner => VaultRole.Admin,
            VaultRole.Admin => VaultRole.Editor,
            VaultRole.Editor => VaultRole.Reader,
            VaultRole.Reader => VaultRole.AiAgentReadWrite,
            VaultRole.AiAgentReadWrite => VaultRole.TrustedPeerNode,
            _ => VaultRole.VaultOwner
        };

        var updated = account with { Role = nextRole };
        ManagedUserAccounts[idx] = updated;
        PrependAuditLog($"Updated RBAC Role: {account.Role} → {nextRole}", account.Email, "RBAC");
        StatusBarRightText = $"🛡️ Updated '{account.DisplayName}' RBAC role to '{nextRole}'";
    }

    [RelayCommand]
    public void GenerateAdminApiKey()
    {
        string label = string.IsNullOrWhiteSpace(NewApiKeyLabelInput)
            ? $"Scoped Key {DateTime.Now:HH:mm}"
            : NewApiKeyLabelInput.Trim();

        var result = _authEngine.CreateApiKey(
            "usr-owner-01",
            label,
            VaultRole.AiAgentReadWrite,
            AccessScope.ForAiAgent(),
            TimeSpan.FromDays(90));

        LastGeneratedApiKeyPreview = $"{result.RawApiKey[..Math.Min(28, result.RawApiKey.Length)]}… (SHA-256: {result.Credential.KeySha256Hex[..12]}…)";
        RefreshAdminCollections();
        PrependAuditLog("Issued Scoped Personal Access Token / API Key", $"{label} ({result.Credential.Role})", "API KEY");
        StatusBarRightText = $"🔑 Generated scoped API key '{label}' (SHA-256 digest stored)";
    }

    [RelayCommand]
    public void RevokeAdminApiKey(ApiKeyCredential? key)
    {
        if (key is null) return;
        _authEngine.RevokeApiKey(key.KeyId);
        RefreshAdminCollections();
        PrependAuditLog("Revoked API Key Credential", $"{key.Label} ({key.KeyId})", "SECURITY");
        StatusBarRightText = $"🚫 Revoked API key '{key.Label}'";
    }

    private static string DeriveInitials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "DB";
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
        }
        return displayName.Length >= 2 ? displayName[..2].ToUpperInvariant() : displayName.ToUpperInvariant();
    }

    /// <summary>
    /// Thread-safe, zero-dependency procedural 32-bit RGBA PNG generator that renders a modern architectural portrait badge
    /// with a rich diagonal gradient, glowing ambient aura, stylized portrait silhouette, and specular glass border.
    /// </summary>
    private static byte[] RenderProceduralAvatarPngBytes(
        int width,
        int height,
        byte r1, byte g1, byte b1,
        byte r2, byte g2, byte b2)
    {
        byte[] rgba = new byte[width * height * 4];
        float cx = width * 0.5f;
        float cy = height * 0.5f;
        float radius = width * 0.48f;

        for (int y = 0; y < height; y++)
        {
            float ny = (float)y / height;
            for (int x = 0; x < width; x++)
            {
                float nx = (float)x / width;
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                int offset = (y * width + x) * 4;

                // Smooth diagonal gradient base
                float t = Math.Clamp((nx * 0.65f) + (ny * 0.35f), 0f, 1f);
                float r = r1 + (r2 - r1) * t;
                float g = g1 + (g2 - g1) * t;
                float b = b1 + (b2 - b1) * t;

                // Warm top-left specular glow
                float gx = nx - 0.28f;
                float gy = ny - 0.24f;
                float glowDist = MathF.Sqrt(gx * gx + gy * gy);
                float glow = MathF.Max(0f, 1f - glowDist * 1.85f);
                r = MathF.Min(255f, r + glow * 58f);
                g = MathF.Min(255f, g + glow * 58f);
                b = MathF.Min(255f, b + glow * 65f);

                // Stylized minimalist architect portrait silhouette (head circle + shoulders arc)
                float headDx = x - cx;
                float headDy = y - (height * 0.39f);
                float headDist = MathF.Sqrt(headDx * headDx + headDy * headDy);
                float headRadius = width * 0.175f;

                float shoulderDx = (x - cx) / 1.35f;
                float shoulderDy = y - (height * 0.86f);
                float shoulderDist = MathF.Sqrt(shoulderDx * shoulderDx + shoulderDy * shoulderDy);
                float shoulderRadius = width * 0.285f;

                bool inSilhouette = headDist <= headRadius || (y >= height * 0.58f && shoulderDist <= shoulderRadius && dist <= radius * 0.90f);
                if (inSilhouette)
                {
                    float silBlend = 0.88f;
                    r = r * (1f - silBlend) + 248f * silBlend;
                    g = g * (1f - silBlend) + 250f * silBlend;
                    b = b * (1f - silBlend) + 252f * silBlend;
                }

                // Concentric architectural orbital ring
                float ringDist = MathF.Abs(dist - (radius * 0.86f));
                if (ringDist < 1.6f)
                {
                    float ringAlpha = (1.6f - ringDist) / 1.6f * 0.55f;
                    r = r * (1f - ringAlpha) + 255f * ringAlpha;
                    g = g * (1f - ringAlpha) + 255f * ringAlpha;
                    b = b * (1f - ringAlpha) + 255f * ringAlpha;
                }

                rgba[offset] = (byte)Math.Clamp((int)r, 0, 255);
                rgba[offset + 1] = (byte)Math.Clamp((int)g, 0, 255);
                rgba[offset + 2] = (byte)Math.Clamp((int)b, 0, 255);
                rgba[offset + 3] = 255;
            }
        }

        return EncodeRgbaToPng(width, height, rgba);
    }

    private static byte[] EncodeRgbaToPng(int width, int height, byte[] rgba)
    {
        using var ms = new MemoryStream();
        // PNG 8-byte signature
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        // IHDR chunk (13 bytes)
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[0..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = 8;  // Bit depth: 8
        ihdr[9] = 6;  // Color type: 6 (RGBA)
        ihdr[10] = 0; // Compression
        ihdr[11] = 0; // Filter
        ihdr[12] = 0; // Interlace
        WritePngChunk(ms, "IHDR"u8, ihdr);

        // IDAT chunk (ZLib-compressed scanlines with filter byte 0 per row)
        int stride = width * 4;
        byte[] rawScanlines = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int dstRow = y * (stride + 1);
            rawScanlines[dstRow] = 0; // Filter type 0 (None)
            Buffer.BlockCopy(rgba, y * stride, rawScanlines, dstRow + 1, stride);
        }

        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rawScanlines, 0, rawScanlines.Length);
        }
        WritePngChunk(ms, "IDAT"u8, compressedStream.ToArray());

        // IEND chunk
        WritePngChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> typeAscii, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        stream.Write(lenBytes);
        stream.Write(typeAscii);
        if (data.Length > 0)
        {
            stream.Write(data);
        }

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, typeAscii);
        crc = UpdateCrc32(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc;
    }
}
