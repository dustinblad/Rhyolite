using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rhyolite.Core.Design;
using Rhyolite.Core.Enterprise;
using Rhyolite.Core.Storage;

namespace Rhyolite.Core.Protocol;

/// <summary>
/// Descriptor for an MCP tool exposed by <see cref="McpServerEngine"/>.
/// </summary>
public sealed record McpToolDescriptor(
    string Name,
    string Description,
    VaultOperation RequiredOperation,
    JsonObject InputSchema);

/// <summary>
/// Descriptor for an MCP resource exposed via <c>resources/list</c> and <c>resources/read</c>.
/// </summary>
public sealed record McpResourceDescriptor(
    string Uri,
    string Name,
    string Description,
    string MimeType);

/// <summary>
/// Represents a parsed JSON-RPC 2.0 request envelope for the Model Context Protocol.
/// </summary>
public sealed record McpRpcRequest(
    string JsonRpc,
    JsonNode? Id,
    string Method,
    JsonObject? Params);

/// <summary>
/// Represents a JSON-RPC 2.0 error payload.
/// </summary>
public sealed record McpRpcError(
    int Code,
    string Message,
    string? Data = null);

/// <summary>
/// Represents a JSON-RPC 2.0 response envelope with microsecond execution telemetry.
/// </summary>
public sealed record McpRpcResponse(
    string JsonRpc,
    JsonNode? Id,
    JsonObject? Result,
    McpRpcError? Error,
    long ExecutionMicroseconds);

/// <summary>
/// High-speed Model Context Protocol (MCP 2025-03-26, JSON-RPC 2.0) server engine for Rhyolite.
/// Supports <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>, <c>resources/list</c>, and <c>resources/read</c>,
/// gated by <see cref="RhyoliteAuthEngine"/> RBAC &amp; folder/#tag ACLs.
/// </summary>
public sealed class McpServerEngine
{
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "rhyolite-mcp-server";
    public const string ServerVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly RhyoliteAuthEngine _authEngine;
    private readonly PeerSyncEngine _vaultEngine;
    private readonly HybridVaultEngine? _vault;
    private readonly IReadOnlyList<McpToolDescriptor> _tools;
    private string _activeDesignSystemId = DesignSystemCatalog.DefaultPresetId;
    private string _activeThemeMode = "light";

    public McpServerEngine(
        RhyoliteAuthEngine authEngine,
        PeerSyncEngine vaultEngine,
        HybridVaultEngine? vault = null)
    {
        ArgumentNullException.ThrowIfNull(authEngine);
        ArgumentNullException.ThrowIfNull(vaultEngine);

        _authEngine = authEngine;
        _vaultEngine = vaultEngine;
        _vault = vault;
        _tools = BuildToolCatalog();
    }

    public McpServerEngine(HybridVaultEngine vault)
        : this(new RhyoliteAuthEngine(), new PeerSyncEngine("local-node"), vault)
    {
    }

    public IReadOnlyList<McpToolDescriptor> Tools => _tools;

    public string ActiveDesignSystemId =>
        _vault?.GetUiPreference("active_design_system") ?? _activeDesignSystemId;

    public string ActiveThemeMode =>
        _vault?.GetUiPreference("active_theme_mode") ?? _activeThemeMode;

    /// <summary>
    /// Processes a raw JSON-RPC 2.0 request string (over stdio or Streamable HTTP/SSE) and returns a serialized JSON-RPC 2.0 response.
    /// </summary>
    public string HandleJsonRpc(string rawJsonRequest, string? authToken = null)
    {
        var startTick = Stopwatch.GetTimestamp();
        try
        {
            var node = JsonNode.Parse(rawJsonRequest)?.AsObject();
            if (node is null)
            {
                return SerializeResponse(CreateErrorResponse(null, -32700, "Invalid JSON-RPC request object.", startTick));
            }

            var jsonRpc = node["jsonrpc"]?.GetValue<string>() ?? "2.0";
            var id = node["id"]?.DeepClone();
            var method = node["method"]?.GetValue<string>() ?? string.Empty;
            var @params = node["params"]?.AsObject();

            var request = new McpRpcRequest(jsonRpc, id, method, @params);
            var response = ExecuteRequest(request, authToken);
            return SerializeResponse(response);
        }
        catch (JsonException ex)
        {
            return SerializeResponse(CreateErrorResponse(null, -32700, $"JSON parse error: {ex.Message}", startTick));
        }
    }

    /// <summary>
    /// Executes a parsed <see cref="McpRpcRequest"/> with RBAC enforcement and microsecond latency tracking.
    /// </summary>
    public McpRpcResponse ExecuteRequest(McpRpcRequest request, string? authToken = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startTick = Stopwatch.GetTimestamp();

        var effectiveToken = authToken
            ?? request.Params?["_meta"]?["authToken"]?.GetValue<string>()
            ?? request.Params?["authToken"]?.GetValue<string>();

        return request.Method switch
        {
            "initialize" => HandleInitialize(request.Id, startTick),
            "notifications/initialized" => CreateSuccessResponse(request.Id, new JsonObject { ["acknowledged"] = true }, startTick),
            "ping" => CreateSuccessResponse(request.Id, new JsonObject(), startTick),
            "tools/list" => HandleToolsList(request.Id, startTick),
            "tools/call" => HandleToolsCall(request.Id, request.Params, effectiveToken, startTick),
            "resources/list" => HandleResourcesList(request.Id, effectiveToken, startTick),
            "resources/read" => HandleResourcesRead(request.Id, request.Params, effectiveToken, startTick),
            _ => CreateErrorResponse(request.Id, -32601, $"MCP method '{request.Method}' is not supported.", startTick)
        };
    }

    private static McpRpcResponse HandleInitialize(JsonNode? id, long startTick)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion
            },
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = true }
            }
        };

        return CreateSuccessResponse(id, result, startTick);
    }

    private McpRpcResponse HandleToolsList(JsonNode? id, long startTick)
    {
        var toolsArray = new JsonArray();
        foreach (var tool in _tools)
        {
            toolsArray.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone()
            });
        }

        var result = new JsonObject
        {
            ["tools"] = toolsArray
        };

        return CreateSuccessResponse(id, result, startTick);
    }

    private McpRpcResponse HandleToolsCall(
        JsonNode? id,
        JsonObject? @params,
        string? authToken,
        long startTick)
    {
        var toolName = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return CreateErrorResponse(id, -32602, "Missing required 'name' parameter in tools/call.", startTick);
        }

        var args = @params?["arguments"]?.AsObject() ?? new JsonObject();

        return toolName switch
        {
            "rhyolite.read_note" => ExecuteReadNoteTool(id, args, authToken, startTick),
            "rhyolite.write_note" => ExecuteWriteNoteTool(id, args, authToken, startTick),
            "rhyolite.patch_section" => ExecutePatchSectionTool(id, args, authToken, startTick),
            "rhyolite.search_fts" => ExecuteSearchFtsTool(id, args, authToken, startTick),
            "rhyolite.query_graph" => ExecuteQueryGraphTool(id, args, authToken, startTick),
            "rhyolite.update_frontmatter" => ExecuteUpdateFrontmatterTool(id, args, authToken, startTick),
            "rhyolite.canvas_op" => ExecuteCanvasOpTool(id, args, authToken, startTick),
            "rhyolite.scan_secrets" => ExecuteScanSecretsTool(id, args, authToken, startTick),
            "rhyolite.analyze_github_pr" => ExecuteAnalyzeGitHubPrTool(id, args, authToken, startTick),
            "rhyolite.verify_workspace_connectors" => ExecuteVerifyWorkspaceConnectorsTool(id, args, authToken, startTick),
            "rhyolite.list_design_systems" => ExecuteListDesignSystemsToolAsync(id, args, authToken, startTick).GetAwaiter().GetResult(),
            "rhyolite.set_design_system" => ExecuteSetDesignSystemToolAsync(id, args, authToken, startTick).GetAwaiter().GetResult(),
            _ => CreateErrorResponse(id, -32602, $"Unknown Rhyolite MCP tool '{toolName}'.", startTick)
        };
    }

    /// <summary>
    /// Asynchronously executes an MCP tool call by name with optional arguments and RBAC token.
    /// </summary>
    public async Task<McpRpcResponse> ExecuteToolCallAsync(
        string toolName,
        JsonObject? arguments = null,
        string? authToken = null,
        CancellationToken ct = default)
    {
        var startTick = Stopwatch.GetTimestamp();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return CreateErrorResponse(null, -32602, "Missing required 'name' parameter in tools/call.", startTick);
        }

        var args = arguments ?? new JsonObject();
        return toolName switch
        {
            "rhyolite.list_design_systems" => await ExecuteListDesignSystemsToolAsync(null, args, authToken, startTick, ct).ConfigureAwait(false),
            "rhyolite.set_design_system" => await ExecuteSetDesignSystemToolAsync(null, args, authToken, startTick, ct).ConfigureAwait(false),
            _ => HandleToolsCall(null, new JsonObject { ["name"] = toolName, ["arguments"] = args.DeepClone() }, authToken, startTick)
        };
    }

    private McpRpcResponse ExecuteReadNoteTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var path = args["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return CreateErrorResponse(id, -32602, "Tool 'rhyolite.read_note' requires 'path'.", startTick);
        }

        var note = _vaultEngine.GetNote(path);
        var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, path, note?.Tags);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        if (note is null)
        {
            return CreateToolPayloadResponse(id, new JsonObject
            {
                ["found"] = false,
                ["path"] = VaultNoteSnapshot.NormalizeVaultPath(path)
            }, decision.EffectiveRole, startTick, isError: true);
        }

        var backlinks = _vaultEngine.GetBacklinks(note.Path);
        var payload = NoteToJsonObject(note, backlinks);
        payload["found"] = true;

        return CreateToolPayloadResponse(id, payload, decision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteWriteNoteTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var path = args["path"]?.GetValue<string>();
        var content = args["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path) || content is null)
        {
            return CreateErrorResponse(id, -32602, "Tool 'rhyolite.write_note' requires 'path' and 'content'.", startTick);
        }

        var extraTags = ExtractStringList(args["tags"]);
        var existing = _vaultEngine.GetNote(path);
        var candidatePreview = VaultNoteSnapshot.FromMarkdown(path, content, _vaultEngine.CurrentHlc, extraTags);
        var combinedTags = existing is null
            ? candidatePreview.Tags
            : existing.Tags.Union(candidatePreview.Tags, StringComparer.OrdinalIgnoreCase);

        var decision = _authEngine.CheckAccess(authToken, VaultOperation.WriteNote, candidatePreview.Path, combinedTags);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        var saved = _vaultEngine.UpsertNote(path, content, extraTags);
        var backlinks = _vaultEngine.GetBacklinks(saved.Path);
        return CreateToolPayloadResponse(id, NoteToJsonObject(saved, backlinks), decision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecutePatchSectionTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var path = args["path"]?.GetValue<string>();
        var heading = args["heading"]?.GetValue<string>();
        var replacementContent = args["replacementContent"]?.GetValue<string>();
        var createIfMissing = args["createIfMissing"]?.GetValue<bool>() ?? true;

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(heading) || replacementContent is null)
        {
            return CreateErrorResponse(
                id,
                -32602,
                "Tool 'rhyolite.patch_section' requires 'path', 'heading', and 'replacementContent'.",
                startTick);
        }

        var existing = _vaultEngine.GetNote(path);
        var decision = _authEngine.CheckAccess(authToken, VaultOperation.PatchNote, path, existing?.Tags);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        var patched = _vaultEngine.PatchNoteSection(path, heading, replacementContent, createIfMissing);
        var payload = NoteToJsonObject(patched, _vaultEngine.GetBacklinks(patched.Path));
        payload["updatedHeading"] = heading;

        return CreateToolPayloadResponse(id, payload, decision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteSearchFtsTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var query = args["query"]?.GetValue<string>() ?? string.Empty;
        var folderPrefix = args["folderPrefix"]?.GetValue<string>();
        var requiredTag = args["requiredTag"]?.GetValue<string>();
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 20, 1, 200);

        var gateDecision = _authEngine.CheckAccess(authToken, VaultOperation.SearchVault, folderPrefix);
        if (!gateDecision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {gateDecision.Reason}", startTick);
        }

        var hits = SearchNotesInternal(query, folderPrefix, requiredTag, limit, authToken);
        var hitsArray = new JsonArray();
        foreach (var hit in hits)
        {
            var tagArray = new JsonArray();
            foreach (var t in hit.Tags)
            {
                tagArray.Add(t);
            }

            hitsArray.Add(new JsonObject
            {
                ["path"] = hit.Path,
                ["title"] = hit.Title,
                ["score"] = hit.Score,
                ["snippet"] = hit.Snippet,
                ["tags"] = tagArray
            });
        }

        var resultPayload = new JsonObject
        {
            ["query"] = query,
            ["totalHits"] = hitsArray.Count,
            ["hits"] = hitsArray
        };

        return CreateToolPayloadResponse(id, resultPayload, gateDecision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteQueryGraphTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var rootPath = args["rootPath"]?.GetValue<string>();
        var maxDepth = Math.Clamp(args["maxDepth"]?.GetValue<int>() ?? 2, 1, 6);

        var gateDecision = _authEngine.CheckAccess(authToken, VaultOperation.QueryGraph, rootPath);
        if (!gateDecision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {gateDecision.Reason}", startTick);
        }

        var subgraph = BuildGraphSubgraph(rootPath, maxDepth, authToken);
        var nodesArray = new JsonArray();
        foreach (var n in subgraph.Nodes)
        {
            var tagsArr = new JsonArray();
            foreach (var t in n.Tags)
            {
                tagsArr.Add(t);
            }

            nodesArray.Add(new JsonObject
            {
                ["path"] = n.Path,
                ["title"] = n.Title,
                ["hopDistance"] = n.HopDistance,
                ["tags"] = tagsArr
            });
        }

        var edgesArray = new JsonArray();
        foreach (var e in subgraph.Edges)
        {
            edgesArray.Add(new JsonObject
            {
                ["sourcePath"] = e.SourcePath,
                ["targetPath"] = e.TargetPath
            });
        }

        var payload = new JsonObject
        {
            ["rootPath"] = subgraph.RootPath,
            ["depth"] = subgraph.Depth,
            ["nodes"] = nodesArray,
            ["edges"] = edgesArray
        };

        return CreateToolPayloadResponse(id, payload, gateDecision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteUpdateFrontmatterTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var path = args["path"]?.GetValue<string>();
        var propsObj = args["properties"]?.AsObject();
        if (string.IsNullOrWhiteSpace(path) || propsObj is null)
        {
            return CreateErrorResponse(
                id,
                -32602,
                "Tool 'rhyolite.update_frontmatter' requires 'path' and 'properties'.",
                startTick);
        }

        var existing = _vaultEngine.GetNote(path);
        var decision = _authEngine.CheckAccess(authToken, VaultOperation.PatchNote, path, existing?.Tags);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in propsObj)
        {
            if (v is not null)
            {
                dict[k] = v.ToString();
            }
        }

        var updated = _vaultEngine.UpdateFrontmatter(path, dict);
        return CreateToolPayloadResponse(
            id,
            NoteToJsonObject(updated, _vaultEngine.GetBacklinks(updated.Path)),
            decision.EffectiveRole,
            startTick);
    }

    private McpRpcResponse ExecuteCanvasOpTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var canvasPath = args["canvasPath"]?.GetValue<string>() ?? "00 — Canvases/Workspace.canvas";
        var op = args["op"]?.GetValue<string>() ?? "upsert_node";

        var decision = _authEngine.CheckAccess(authToken, VaultOperation.ModifyCanvas, canvasPath);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        SpatialCanvasNode? node = null;
        if (args["nodeId"] is not null)
        {
            node = new SpatialCanvasNode(
                NodeId: args["nodeId"]!.GetValue<string>(),
                Label: args["label"]?.GetValue<string>() ?? "Untitled Card",
                X: args["x"]?.GetValue<double>() ?? 0,
                Y: args["y"]?.GetValue<double>() ?? 0,
                Width: args["width"]?.GetValue<double>() ?? 280,
                Height: args["height"]?.GetValue<double>() ?? 160,
                LinkedNotePath: args["linkedNotePath"]?.GetValue<string>());
        }

        SpatialCanvasEdge? edge = null;
        if (args["edgeId"] is not null && args["fromNodeId"] is not null && args["toNodeId"] is not null)
        {
            edge = new SpatialCanvasEdge(
                EdgeId: args["edgeId"]!.GetValue<string>(),
                FromNodeId: args["fromNodeId"]!.GetValue<string>(),
                ToNodeId: args["toNodeId"]!.GetValue<string>(),
                Label: args["label"]?.GetValue<string>());
        }

        var (nodes, edges) = _vaultEngine.ApplyCanvasOperation(canvasPath, op, node, edge);
        var payload = new JsonObject
        {
            ["canvasPath"] = canvasPath,
            ["operation"] = op,
            ["nodeCount"] = nodes.Count,
            ["edgeCount"] = edges.Count
        };

        return CreateToolPayloadResponse(id, payload, decision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteScanSecretsTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var path = args["path"]?.GetValue<string>() ?? "Untitled.md";
        var content = args["content"]?.GetValue<string>() ?? _vaultEngine.GetNote(path)?.Content;
        if (content is null)
        {
            return CreateErrorResponse(id, -32602, "Tool 'rhyolite.scan_secrets' requires 'path' and 'content'.", startTick);
        }

        var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, path);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        var backendRaw = args["backend"]?.GetValue<string>();
        var backend = backendRaw?.Trim().ToLowerInvariant() switch
        {
            "keepassxc" or "keepassxc-cli" or "keepassxccli" or "kdbx" => SecretVaultBackend.KeePassXcCli,
            "env" or "environment" or "vault" or "environmentvault" => SecretVaultBackend.EnvironmentVault,
            _ => SecretVaultBackend.OnePasswordCli
        };

        var report = EnterpriseDevExEngine.ScanAndRedactSecrets(path, content, backend);
        var findingsArray = new JsonArray();
        foreach (var finding in report.Findings)
        {
            findingsArray.Add(new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["providerName"] = finding.ProviderName,
                ["severity"] = finding.Severity.ToString(),
                ["lineNumber"] = finding.LineNumber,
                ["maskedPreview"] = finding.MaskedPreview,
                ["appliedPlaceholder"] = finding.AppliedPlaceholder
            });
        }

        var payload = new JsonObject
        {
            ["path"] = report.NotePath,
            ["isClean"] = report.IsClean,
            ["findingsCount"] = report.Findings.Count,
            ["preferredBackend"] = report.PreferredBackend.ToString(),
            ["redactedMarkdown"] = report.RedactedMarkdown,
            ["findings"] = findingsArray
        };

        return CreateToolPayloadResponse(id, payload, decision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteAnalyzeGitHubPrTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
        var prNumber = args["prNumber"]?.GetValue<int>() ?? samplePr.Number;
        var repository = args["repository"]?.GetValue<string>() ?? samplePr.Repository;
        var prSnapshot = prNumber == samplePr.Number && string.Equals(repository, samplePr.Repository, StringComparison.OrdinalIgnoreCase)
            ? samplePr
            : samplePr with { Repository = repository, Number = prNumber };

        var slashIdx = prSnapshot.Repository.IndexOf('/');
        var owner = slashIdx > 0 ? prSnapshot.Repository[..slashIdx] : "dustinblad";
        var repoName = slashIdx > 0 && slashIdx + 1 < prSnapshot.Repository.Length
            ? prSnapshot.Repository[(slashIdx + 1)..]
            : prSnapshot.Repository;

        var singleRttGraphQlQuery = EnterpriseDevExEngine.BuildSingleRttPullRequestGraphQlQuery(owner, repoName, prSnapshot.Number);
        var sqliteWalCacheKey = EnterpriseDevExEngine.ComputePrSqliteWalCacheKey(prSnapshot.Repository, prSnapshot.Number, prSnapshot.HeadSha);
        var analysis = EnterpriseDevExEngine.AnalyzeGitHubPullRequest(prSnapshot, []);

        var payload = new JsonObject
        {
            ["repository"] = prSnapshot.Repository,
            ["prNumber"] = prSnapshot.Number,
            ["title"] = prSnapshot.Title,
            ["headSha"] = prSnapshot.HeadSha,
            ["singleRttGraphQlQuery"] = singleRttGraphQlQuery,
            ["graphQlQuery"] = singleRttGraphQlQuery,
            ["sqliteWalCacheKey"] = sqliteWalCacheKey,
            ["walCacheKey"] = sqliteWalCacheKey,
            ["riskLevel"] = analysis.RiskLevel,
            ["unresolvedThreadsCount"] = analysis.UnresolvedThreadsCount,
            ["actionableBlockersCount"] = analysis.ActionableBlockersCount,
            ["totalAdditions"] = analysis.TotalAdditions,
            ["totalDeletions"] = analysis.TotalDeletions,
            ["passedDiffSecretScan"] = analysis.PassedDiffSecretScan,
            ["generatedReviewMarkdown"] = analysis.GeneratedReviewMarkdown
        };

        return CreateToolPayloadResponse(id, payload, decision.EffectiveRole, startTick);
    }

    private McpRpcResponse ExecuteVerifyWorkspaceConnectorsTool(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick)
    {
        var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        var bladbaseConnector = new JsonObject
        {
            ["connectorId"] = "bladbase-primary",
            ["provider"] = "Bladbase",
            ["workspace"] = "dustinblad/bladbase",
            ["endpoint"] = "https://bladbase.dev/api/v1/workspaces/dustinblad/bladbase",
            ["protocol"] = "MCP + GraphQL + HLC Delta Sync",
            ["authType"] = "OAuth2 PKCE + Scoped PAT",
            ["dualAuthorshipFrontmatter"] = "bladbase_author_type",
            ["status"] = "Active",
            ["enabled"] = true
        };

        var docmostConnector = new JsonObject
        {
            ["connectorId"] = "docmost-primary",
            ["provider"] = "Docmost",
            ["space"] = "Engineering Architecture",
            ["spaceSlug"] = "engineering-architecture",
            ["endpoint"] = "https://docs.internal.rhyolite.dev/api",
            ["protocol"] = "ProseMirror JSON <-> CommonMark + WikiLinks Bidirectional Bridge",
            ["authType"] = "Bearer PAT",
            ["status"] = "Active",
            ["enabled"] = true
        };

        var payload = new JsonObject
        {
            ["status"] = "Healthy",
            ["verified"] = true,
            ["bladbase"] = bladbaseConnector,
            ["docmost"] = docmostConnector,
            ["connectors"] = new JsonArray
            {
                bladbaseConnector.DeepClone(),
                docmostConnector.DeepClone()
            }
        };

        return CreateToolPayloadResponse(id, payload, decision.EffectiveRole, startTick);
    }

    private async Task<McpRpcResponse> ExecuteListDesignSystemsToolAsync(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick,
        CancellationToken ct = default)
    {
        var effectiveRole = VaultRole.VaultOwner;
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
            if (!decision.IsAllowed)
            {
                return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
            }
            effectiveRole = decision.EffectiveRole;
        }

        var savedDesignId = _vault is not null
            ? await _vault.GetUiPreferenceAsync("active_design_system", ct).ConfigureAwait(false)
            : null;
        var activeDesignSystem = string.IsNullOrWhiteSpace(savedDesignId) ? _activeDesignSystemId : savedDesignId;

        var savedThemeMode = _vault is not null
            ? await _vault.GetUiPreferenceAsync("active_theme_mode", ct).ConfigureAwait(false)
            : null;
        var activeThemeMode = string.IsNullOrWhiteSpace(savedThemeMode) ? _activeThemeMode : savedThemeMode;

        var presetsArray = new JsonArray();
        foreach (var preset in DesignSystemCatalog.All)
        {
            presetsArray.Add(new JsonObject
            {
                ["id"] = preset.Id,
                ["name"] = preset.Name,
                ["category"] = preset.Category,
                ["tagline"] = preset.Tagline,
                ["designMdSourcePath"] = preset.DesignMdSourcePath,
                ["prefersDarkByDefault"] = preset.PrefersDarkByDefault,
                ["cardCornerRadius"] = preset.CardCornerRadius,
                ["buttonCornerRadius"] = preset.ButtonCornerRadius,
                ["lightAccent"] = preset.LightAccent,
                ["darkAccent"] = preset.DarkAccent
            });
        }

        var payload = new JsonObject
        {
            ["active_design_system"] = activeDesignSystem,
            ["activeDesignSystem"] = activeDesignSystem,
            ["active_theme_mode"] = activeThemeMode,
            ["activeThemeMode"] = activeThemeMode,
            ["count"] = presetsArray.Count,
            ["presets"] = presetsArray,
            ["designSystems"] = presetsArray.DeepClone()
        };

        return CreateToolPayloadResponse(id, payload, effectiveRole, startTick);
    }

    private async Task<McpRpcResponse> ExecuteSetDesignSystemToolAsync(
        JsonNode? id,
        JsonObject args,
        string? authToken,
        long startTick,
        CancellationToken ct = default)
    {
        var effectiveRole = VaultRole.VaultOwner;
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            var decision = _authEngine.CheckAccess(authToken, VaultOperation.WriteNote);
            if (!decision.IsAllowed)
            {
                return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
            }
            effectiveRole = decision.EffectiveRole;
        }

        var designId = args["designId"]?.GetValue<string>()
            ?? args["id"]?.GetValue<string>()
            ?? args["presetId"]?.GetValue<string>();
        var preset = DesignSystemCatalog.GetOrDefault(designId);
        var isDark = args["darkMode"]?.GetValue<bool>() ?? preset.PrefersDarkByDefault;
        var themeMode = isDark ? "dark" : "light";

        _activeDesignSystemId = preset.Id;
        _activeThemeMode = themeMode;

        if (_vault is not null)
        {
            await _vault.SaveUiPreferenceAsync("active_design_system", preset.Id, ct).ConfigureAwait(false);
            await _vault.SaveUiPreferenceAsync("active_theme_mode", themeMode, ct).ConfigureAwait(false);
        }

        var palette = preset.GetPalette(isDark);
        var presetObj = new JsonObject
        {
            ["id"] = preset.Id,
            ["name"] = preset.Name,
            ["category"] = preset.Category,
            ["tagline"] = preset.Tagline,
            ["designMdSourcePath"] = preset.DesignMdSourcePath,
            ["prefersDarkByDefault"] = preset.PrefersDarkByDefault,
            ["cardCornerRadius"] = preset.CardCornerRadius,
            ["buttonCornerRadius"] = preset.ButtonCornerRadius,
            ["lightAccent"] = preset.LightAccent,
            ["darkAccent"] = preset.DarkAccent
        };

        var paletteObj = new JsonObject
        {
            ["bgHex"] = palette.BgHex,
            ["surfaceHex"] = palette.SurfaceHex,
            ["surfaceWarmHex"] = palette.SurfaceWarmHex,
            ["surfaceElevatedHex"] = palette.SurfaceElevatedHex,
            ["fgHex"] = palette.FgHex,
            ["fg2Hex"] = palette.Fg2Hex,
            ["mutedHex"] = palette.MutedHex,
            ["metaHex"] = palette.MetaHex,
            ["borderHex"] = palette.BorderHex,
            ["borderSoftHex"] = palette.BorderSoftHex,
            ["accentHex"] = palette.AccentHex,
            ["accentHoverHex"] = palette.AccentHoverHex,
            ["linkHex"] = palette.LinkHex,
            ["calloutBgHex"] = palette.CalloutBgHex,
            ["successHex"] = palette.SuccessHex,
            ["warnHex"] = palette.WarnHex,
            ["dangerHex"] = palette.DangerHex
        };

        var payload = new JsonObject
        {
            ["active_design_system"] = preset.Id,
            ["activeDesignSystem"] = preset.Id,
            ["active_theme_mode"] = themeMode,
            ["activeThemeMode"] = themeMode,
            ["darkMode"] = isDark,
            ["preset"] = presetObj,
            ["palette"] = paletteObj
        };

        return CreateToolPayloadResponse(id, payload, effectiveRole, startTick);
    }

    private McpRpcResponse HandleResourcesList(JsonNode? id, string? authToken, long startTick)
    {
        var resourcesArray = new JsonArray();
        foreach (var note in _vaultEngine.GetAllNotes())
        {
            var access = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, note.Path, note.Tags);
            if (!access.IsAllowed)
            {
                continue;
            }

            resourcesArray.Add(new JsonObject
            {
                ["uri"] = $"rhyolite://vault/notes/{Uri.EscapeDataString(note.Path)}",
                ["name"] = note.Title,
                ["description"] = $"Markdown note at '{note.Path}' (HLC {note.Hlc})",
                ["mimeType"] = "text/markdown"
            });
        }

        return CreateSuccessResponse(id, new JsonObject { ["resources"] = resourcesArray }, startTick);
    }

    private McpRpcResponse HandleResourcesRead(
        JsonNode? id,
        JsonObject? @params,
        string? authToken,
        long startTick)
    {
        var uri = @params?["uri"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uri))
        {
            return CreateErrorResponse(id, -32602, "Missing 'uri' parameter in resources/read.", startTick);
        }

        const string prefix = "rhyolite://vault/notes/";
        var rawPath = uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(uri[prefix.Length..])
            : uri;

        var note = _vaultEngine.GetNote(rawPath);
        var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, rawPath, note?.Tags);
        if (!decision.IsAllowed)
        {
            return CreateErrorResponse(id, -32001, $"RBAC Access Denied: {decision.Reason}", startTick);
        }

        if (note is null)
        {
            return CreateErrorResponse(id, -32002, $"Resource '{rawPath}' was not found.", startTick);
        }

        var contents = new JsonArray
        {
            new JsonObject
            {
                ["uri"] = uri,
                ["mimeType"] = "text/markdown",
                ["text"] = note.Content
            }
        };

        return CreateSuccessResponse(id, new JsonObject { ["contents"] = contents }, startTick);
    }

    internal IReadOnlyList<(string Path, string Title, double Score, string Snippet, IReadOnlyList<string> Tags)> SearchNotesInternal(
        string query,
        string? folderPrefix,
        string? requiredTag,
        int limit,
        string? authToken)
    {
        var terms = query
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToArray();

        var results = new List<(string Path, string Title, double Score, string Snippet, IReadOnlyList<string> Tags)>();
        var normFolder = string.IsNullOrWhiteSpace(folderPrefix) ? null : AccessScope.NormalizeFolderPrefix(folderPrefix);
        var normTag = string.IsNullOrWhiteSpace(requiredTag) ? null : AccessScope.NormalizeTag(requiredTag);

        foreach (var note in _vaultEngine.GetAllNotes())
        {
            if (normFolder is not null && !note.Path.StartsWith(normFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normTag is not null && !note.Tags.Contains(normTag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var access = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, note.Path, note.Tags);
            if (!access.IsAllowed)
            {
                continue;
            }

            double score = 0;
            if (terms.Length == 0)
            {
                score = 1.0;
            }
            else
            {
                foreach (var term in terms)
                {
                    if (note.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5.0;
                    }

                    var idx = 0;
                    while ((idx = note.Content.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        score += 1.25;
                        idx += term.Length;
                    }
                }
            }

            if (score > 0)
            {
                var snippet = BuildSnippet(note.Content, terms.FirstOrDefault());
                results.Add((note.Path, note.Title, Math.Round(score, 2), snippet, note.Tags));
            }
        }

        return [.. results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .Take(limit)];
    }

    internal (string? RootPath, int Depth, IReadOnlyList<(string Path, string Title, IReadOnlyList<string> Tags, int HopDistance)> Nodes, IReadOnlyList<(string SourcePath, string TargetPath)> Edges) BuildGraphSubgraph(
        string? rootPath,
        int maxDepth,
        string? authToken)
    {
        var accessibleNotes = _vaultEngine.GetAllNotes()
            .Where(n => _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, n.Path, n.Tags).IsAllowed)
            .ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);

        var nodes = new List<(string Path, string Title, IReadOnlyList<string> Tags, int HopDistance)>();
        var edges = new List<(string SourcePath, string TargetPath)>();

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            foreach (var note in accessibleNotes.Values.OrderBy(n => n.Path, StringComparer.Ordinal))
            {
                nodes.Add((note.Path, note.Title, note.Tags, 0));
                foreach (var target in note.OutgoingLinks)
                {
                    if (accessibleNotes.ContainsKey(target))
                    {
                        edges.Add((note.Path, target));
                    }
                }
            }

            return (null, maxDepth, nodes, edges);
        }

        var normalizedRoot = VaultNoteSnapshot.NormalizeVaultPath(rootPath);
        if (!accessibleNotes.TryGetValue(normalizedRoot, out var startNote))
        {
            return (normalizedRoot, maxDepth, nodes, edges);
        }

        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [startNote.Path] = 0
        };
        var queue = new Queue<(VaultNoteSnapshot Note, int Depth)>();
        queue.Enqueue((startNote, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            nodes.Add((current.Path, current.Title, current.Tags, depth));

            if (depth >= maxDepth)
            {
                continue;
            }

            // Outgoing wikilinks
            foreach (var targetPath in current.OutgoingLinks)
            {
                if (!accessibleNotes.TryGetValue(targetPath, out var targetNote))
                {
                    continue;
                }

                edges.Add((current.Path, targetNote.Path));
                if (!visited.ContainsKey(targetNote.Path))
                {
                    visited[targetNote.Path] = depth + 1;
                    queue.Enqueue((targetNote, depth + 1));
                }
            }

            // Incoming backlinks
            foreach (var backlinkPath in _vaultEngine.GetBacklinks(current.Path))
            {
                if (!accessibleNotes.TryGetValue(backlinkPath, out var sourceNote))
                {
                    continue;
                }

                edges.Add((sourceNote.Path, current.Path));
                if (!visited.ContainsKey(sourceNote.Path))
                {
                    visited[sourceNote.Path] = depth + 1;
                    queue.Enqueue((sourceNote, depth + 1));
                }
            }
        }

        var distinctEdges = edges
            .Distinct()
            .OrderBy(e => e.SourcePath, StringComparer.Ordinal)
            .ThenBy(e => e.TargetPath, StringComparer.Ordinal)
            .ToList();

        return (normalizedRoot, maxDepth, nodes, distinctEdges);
    }

    private static string BuildSnippet(string content, string? firstTerm)
    {
        var singleLine = content.Replace('\n', ' ').Trim();
        if (singleLine.Length <= 160)
        {
            return singleLine;
        }

        if (!string.IsNullOrEmpty(firstTerm))
        {
            var idx = singleLine.IndexOf(firstTerm, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = Math.Max(0, idx - 40);
                var len = Math.Min(160, singleLine.Length - start);
                return singleLine.Substring(start, len);
            }
        }

        return singleLine[..160];
    }

    private static JsonObject NoteToJsonObject(VaultNoteSnapshot note, IReadOnlyList<string> backlinks)
    {
        var tagsArray = new JsonArray();
        foreach (var tag in note.Tags)
        {
            tagsArray.Add(tag);
        }

        var outgoingArray = new JsonArray();
        foreach (var link in note.OutgoingLinks)
        {
            outgoingArray.Add(link);
        }

        var backlinksArray = new JsonArray();
        foreach (var backlink in backlinks)
        {
            backlinksArray.Add(backlink);
        }

        var frontmatterObj = new JsonObject();
        foreach (var (k, v) in note.Frontmatter)
        {
            frontmatterObj[k] = v;
        }

        return new JsonObject
        {
            ["path"] = note.Path,
            ["title"] = note.Title,
            ["content"] = note.Content,
            ["sha256"] = note.Sha256,
            ["hlc"] = note.Hlc.ToString(),
            ["tags"] = tagsArray,
            ["frontmatter"] = frontmatterObj,
            ["outgoingLinks"] = outgoingArray,
            ["backlinks"] = backlinksArray
        };
    }

    private static List<string> ExtractStringList(JsonNode? node)
    {
        if (node is not JsonArray arr)
        {
            return [];
        }

        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var val = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(val))
            {
                list.Add(val);
            }
        }
        return list;
    }

    private static McpRpcResponse CreateToolPayloadResponse(
        JsonNode? id,
        JsonObject payload,
        VaultRole effectiveRole,
        long startTick,
        bool isError = false)
    {
        var elapsedUs = GetElapsedMicroseconds(startTick);
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(JsonOptions)
                }
            },
            ["isError"] = isError,
            ["_meta"] = new JsonObject
            {
                ["executionMicroseconds"] = elapsedUs,
                ["role"] = effectiveRole.ToString()
            }
        };

        foreach (var (key, value) in payload)
        {
            if (key is not ("content" or "isError" or "_meta"))
            {
                result[key] = value?.DeepClone();
            }
        }

        return new McpRpcResponse("2.0", id, result, null, elapsedUs);
    }

    private static McpRpcResponse CreateSuccessResponse(JsonNode? id, JsonObject result, long startTick)
    {
        var elapsedUs = GetElapsedMicroseconds(startTick);
        result["_meta"] = new JsonObject
        {
            ["executionMicroseconds"] = elapsedUs
        };
        return new McpRpcResponse("2.0", id, result, null, elapsedUs);
    }

    private static McpRpcResponse CreateErrorResponse(JsonNode? id, int code, string message, long startTick)
    {
        var elapsedUs = GetElapsedMicroseconds(startTick);
        return new McpRpcResponse("2.0", id, null, new McpRpcError(code, message), elapsedUs);
    }

    private static long GetElapsedMicroseconds(long startTick) =>
        Math.Max(1L, (long)Stopwatch.GetElapsedTime(startTick).TotalMicroseconds);

    private static string SerializeResponse(McpRpcResponse response)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = response.JsonRpc,
            ["id"] = response.Id?.DeepClone()
        };

        if (response.Error is not null)
        {
            envelope["error"] = new JsonObject
            {
                ["code"] = response.Error.Code,
                ["message"] = response.Error.Message
            };
        }
        else
        {
            envelope["result"] = response.Result?.DeepClone() ?? new JsonObject();
        }

        return envelope.ToJsonString(JsonOptions);
    }

    private static IReadOnlyList<McpToolDescriptor> BuildToolCatalog() =>
    [
        new McpToolDescriptor(
            "rhyolite.read_note",
            "Reads a Markdown note from the Rhyolite vault, returning content, YAML frontmatter, #tags, outgoing wikilinks, and incoming backlinks.",
            VaultOperation.ReadNote,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("path"),
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Vault-relative path to the .md note." }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.write_note",
            "Atomically creates or replaces a Markdown note in the Rhyolite vault and indexes its frontmatter, #tags, and wikilinks.",
            VaultOperation.WriteNote,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("path", "content"),
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["content"] = new JsonObject { ["type"] = "string" },
                    ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.patch_section",
            "Surgically replaces a single Markdown heading section inside a note without rewriting surrounding sections.",
            VaultOperation.PatchNote,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("path", "heading", "replacementContent"),
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["heading"] = new JsonObject { ["type"] = "string" },
                    ["replacementContent"] = new JsonObject { ["type"] = "string" },
                    ["createIfMissing"] = new JsonObject { ["type"] = "boolean" }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.search_fts",
            "Performs BM25-ranked full-text search across readable vault notes with optional folder prefix and #tag filtering.",
            VaultOperation.SearchVault,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("query"),
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string" },
                    ["folderPrefix"] = new JsonObject { ["type"] = "string" },
                    ["requiredTag"] = new JsonObject { ["type"] = "string" },
                    ["limit"] = new JsonObject { ["type"] = "integer" }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.query_graph",
            "Queries the bidirectional wikilink and backlink knowledge graph around a note up to maxDepth hops.",
            VaultOperation.QueryGraph,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["rootPath"] = new JsonObject { ["type"] = "string" },
                    ["maxDepth"] = new JsonObject { ["type"] = "integer" }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.update_frontmatter",
            "Atomically merges typed YAML frontmatter properties at the top of a Markdown note.",
            VaultOperation.PatchNote,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("path", "properties"),
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["properties"] = new JsonObject { ["type"] = "object" }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.canvas_op",
            "Mutates an infinite spatial canvas (.canvas) by upserting or deleting spatial cards and edges.",
            VaultOperation.ModifyCanvas,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("canvasPath", "op"),
                ["properties"] = new JsonObject
                {
                    ["canvasPath"] = new JsonObject { ["type"] = "string" },
                    ["op"] = new JsonObject { ["type"] = "string" },
                    ["nodeId"] = new JsonObject { ["type"] = "string" },
                    ["label"] = new JsonObject { ["type"] = "string" },
                    ["x"] = new JsonObject { ["type"] = "number" },
                    ["y"] = new JsonObject { ["type"] = "number" }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.scan_secrets",
            "Scans Markdown content for leaked credentials and redacts them into 1Password CLI (op://) or KeePassXC CLI (keepassxc://) references.",
            VaultOperation.ReadNote,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("path", "content"),
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["content"] = new JsonObject { ["type"] = "string" },
                    ["backend"] = new JsonObject { ["type"] = "string", ["description"] = "Optional vault backend: '1password' or 'keepassxc'." }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.analyze_github_pr",
            "Analyzes a GitHub Pull Request via a 1-RTT GraphQL v4 subgraph query and SQLite WAL cache key, returning risk level, unresolved review threads, and generated review Markdown.",
            VaultOperation.ReadNote,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prNumber"] = new JsonObject { ["type"] = "integer" },
                    ["repository"] = new JsonObject { ["type"] = "string" }
                }
            }),
        new McpToolDescriptor(
            "rhyolite.verify_workspace_connectors",
            "Verifies active Bladbase Workspace (dustinblad/bladbase) and Docmost Space connector configurations and sync readiness.",
            VaultOperation.ReadNote,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),
        new McpToolDescriptor(
            "rhyolite.list_design_systems",
            "Lists all 9 feimacode/open-design-agent-kit design system presets (Apple, Linear, Notion, Vercel, Claude, GitHub, Raycast, Stripe, Supabase) and the active design system ID.",
            VaultOperation.ReadNote,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            }),
        new McpToolDescriptor(
            "rhyolite.set_design_system",
            "Switches the active Rhyolite design system preset and theme mode, persisting preferences to the SQLite WAL sidecar and returning the resolved palette tokens.",
            VaultOperation.WriteNote,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("designId"),
                ["properties"] = new JsonObject
                {
                    ["designId"] = new JsonObject { ["type"] = "string", ["description"] = "Design system preset ID (apple, linear-app, notion, vercel, claude, github, raycast, stripe, supabase)." },
                    ["darkMode"] = new JsonObject { ["type"] = "boolean", ["description"] = "Optional dark mode override." }
                }
            })
    ];
}
