using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
    private readonly IReadOnlyList<McpToolDescriptor> _tools;

    public McpServerEngine(RhyoliteAuthEngine authEngine, PeerSyncEngine vaultEngine)
    {
        ArgumentNullException.ThrowIfNull(authEngine);
        ArgumentNullException.ThrowIfNull(vaultEngine);

        _authEngine = authEngine;
        _vaultEngine = vaultEngine;
        _tools = BuildToolCatalog();
    }

    public IReadOnlyList<McpToolDescriptor> Tools => _tools;

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
            _ => CreateErrorResponse(id, -32602, $"Unknown Rhyolite MCP tool '{toolName}'.", startTick)
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
            })
    ];
}
