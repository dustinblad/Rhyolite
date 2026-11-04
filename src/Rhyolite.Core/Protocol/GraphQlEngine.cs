using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rhyolite.Core.Design;
using Rhyolite.Core.Enterprise;
using Rhyolite.Core.Storage;

namespace Rhyolite.Core.Protocol;

/// <summary>
/// Represents a GraphQL request envelope (query/mutation document, optional operationName, and variables).
/// </summary>
public sealed record GraphQlRequest(
    string Query,
    string? OperationName = null,
    JsonObject? Variables = null);

/// <summary>
/// Represents a structured GraphQL execution error.
/// </summary>
public sealed record GraphQlError(
    string Message,
    string Code = "GRAPHQL_EXECUTION_ERROR");

/// <summary>
/// Represents a GraphQL response envelope containing <c>data</c>, optional <c>errors</c>, and <c>extensions</c> telemetry.
/// </summary>
public sealed record GraphQlResponse(
    JsonObject? Data,
    IReadOnlyList<GraphQlError> Errors,
    long ExecutionMicroseconds)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Lightweight, high-speed GraphQL schema parser and execution engine for Rhyolite.
/// Supports <see cref="GetSchemaSdl"/> introspection, 1-RTT nested queries (<c>note</c>, <c>notes</c>, <c>graph</c>,
/// <c>search</c>, <c>peers</c>, <c>authProviders</c>), and mutations (<c>upsertNote</c>, <c>patchNote</c>, <c>deleteNote</c>,
/// <c>createApiKey</c>, <c>connectPeer</c>) with full <see cref="RhyoliteAuthEngine"/> RBAC enforcement.
/// </summary>
public sealed partial class GraphQlEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RhyoliteAuthEngine _authEngine;
    private readonly PeerSyncEngine _vaultEngine;
    private readonly HybridVaultEngine? _vault;
    private readonly McpServerEngine _mcpEngine;

    public GraphQlEngine(
        RhyoliteAuthEngine authEngine,
        PeerSyncEngine vaultEngine,
        McpServerEngine? mcpEngine = null,
        HybridVaultEngine? vault = null)
    {
        ArgumentNullException.ThrowIfNull(authEngine);
        ArgumentNullException.ThrowIfNull(vaultEngine);

        _authEngine = authEngine;
        _vaultEngine = vaultEngine;
        _vault = vault;
        _mcpEngine = mcpEngine ?? new McpServerEngine(authEngine, vaultEngine, vault);
    }

    public GraphQlEngine(HybridVaultEngine vault)
        : this(new RhyoliteAuthEngine(), new PeerSyncEngine("local-node"), null, vault)
    {
    }

    /// <summary>
    /// Returns the canonical GraphQL Schema Definition Language (SDL) for Rhyolite.
    /// </summary>
    public string GetSchemaSdl() => """
        schema {
          query: Query
          mutation: Mutation
          subscription: Subscription
        }

        type Query {
          note(path: String!): Note
          notes(folderPrefix: String, tag: String, limit: Int = 50): [Note!]!
          graph(rootPath: String, depth: Int = 2): GraphSubgraph!
          search(query: String!, limit: Int = 20): [SearchHit!]!
          peers: [PeerNode!]!
          authProviders: [OAuthProviderDescriptor!]!
          devExPrPreview(prNumber: Int = 142): DevExPrPreview!
          workspaceConnectors: [WorkspaceConnectorDescriptor!]!
          designSystems: [DesignSystemPresetDescriptor!]!
          activeDesignSystem: String!
        }

        type Mutation {
          upsertNote(path: String!, content: String!, tags: [String!]): Note!
          patchNote(path: String!, heading: String!, content: String!): Note!
          deleteNote(path: String!): Boolean!
          createApiKey(label: String!, role: String!, allowedFolders: [String!], deniedTags: [String!]): ApiKeyCreationResult!
          connectPeer(nodeId: String!, displayName: String!, endpoint: String!): PeerNode!
        }

        type Subscription {
          noteUpdated(folderPrefix: String): NoteDeltaEvent!
          peerSyncStateChanged: PeerNode!
        }

        type Note {
          path: String!
          title: String!
          content: String!
          sha256: String!
          hlcTimestamp: String!
          tags: [String!]!
          frontmatter: [FrontmatterEntry!]!
          outgoingLinks: [String!]!
          backlinks: [String!]!
        }

        type FrontmatterEntry {
          key: String!
          value: String!
        }

        type GraphSubgraph {
          rootPath: String
          depth: Int!
          nodes: [GraphNode!]!
          edges: [GraphEdge!]!
        }

        type GraphNode {
          path: String!
          title: String!
          tags: [String!]!
          hopDistance: Int!
        }

        type GraphEdge {
          sourcePath: String!
          targetPath: String!
        }

        type SearchHit {
          path: String!
          title: String!
          score: Float!
          snippet: String!
          tags: [String!]!
        }

        type PeerNode {
          nodeId: String!
          displayName: String!
          endpoint: String!
          lastKnownHlc: String!
          lastRootMerkleHash: String!
          status: String!
        }

        type OAuthProviderDescriptor {
          providerType: String!
          displayName: String!
          authorizationEndpoint: String!
          tokenEndpoint: String!
          supportsPkce: Boolean!
          enabled: Boolean!
        }

        type ApiKeyCreationResult {
          keyId: String!
          label: String!
          role: String!
          rawApiKey: String!
          expiresAtUtc: String!
        }

        type NoteDeltaEvent {
          path: String!
          changeType: String!
          hlcTimestamp: String!
          sha256: String!
        }

        type DevExPrPreview {
          repository: String!
          prNumber: Int!
          title: String!
          authorLogin: String!
          headBranch: String!
          baseBranch: String!
          headSha: String!
          state: String!
          ciCheckStatus: String!
          reviewDecision: String!
          riskLevel: String!
          unresolvedThreadsCount: Int!
          actionableBlockersCount: Int!
          totalAdditions: Int!
          totalDeletions: Int!
          sqliteWalCacheKey: String!
          singleRttGraphQlQuery: String!
          generatedReviewMarkdown: String!
        }

        type WorkspaceConnectorDescriptor {
          connectorId: String!
          provider: String!
          workspace: String
          space: String
          workspaceOrSpace: String!
          endpoint: String!
          protocol: String!
          status: String!
          enabled: Boolean!
        }

        type DesignSystemPresetDescriptor {
          id: String!
          name: String!
          category: String!
          tagline: String!
          designMdSourcePath: String!
          prefersDarkByDefault: Boolean!
          cardCornerRadius: Float!
          buttonCornerRadius: Float!
          lightAccent: String!
          darkAccent: String!
          isActive: Boolean!
        }
        """;

    /// <summary>
    /// Executes a raw HTTP JSON GraphQL request payload (<c>{ "query": "...", "variables": { ... } }</c>).
    /// </summary>
    public string ExecuteJsonHttp(string rawJsonBody, string? authToken = null)
    {
        var startTick = Stopwatch.GetTimestamp();
        try
        {
            var body = JsonNode.Parse(rawJsonBody)?.AsObject();
            if (body is null)
            {
                return SerializeGraphQlResponse(new GraphQlResponse(
                    null,
                    [new GraphQlError("Invalid JSON request body.", "BAD_REQUEST")],
                    GetElapsedMicroseconds(startTick)));
            }

            var query = body["query"]?.GetValue<string>() ?? string.Empty;
            var opName = body["operationName"]?.GetValue<string>();
            var variables = body["variables"]?.AsObject();

            var response = Execute(new GraphQlRequest(query, opName, variables), authToken);
            return SerializeGraphQlResponse(response);
        }
        catch (JsonException ex)
        {
            return SerializeGraphQlResponse(new GraphQlResponse(
                null,
                [new GraphQlError($"Malformed JSON payload: {ex.Message}", "JSON_PARSE_ERROR")],
                GetElapsedMicroseconds(startTick)));
        }
    }

    /// <summary>
    /// Executes a GraphQL query or mutation document against the Rhyolite vault with RBAC enforcement.
    /// </summary>
    public GraphQlResponse Execute(GraphQlRequest request, string? authToken = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startTick = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new GraphQlResponse(
                null,
                [new GraphQlError("GraphQL query document cannot be empty.", "EMPTY_QUERY")],
                GetElapsedMicroseconds(startTick));
        }

        var queryText = request.Query.Trim();
        var data = new JsonObject();
        var errors = new List<GraphQlError>();
        var variables = request.Variables ?? new JsonObject();

        // Introspection shortcut
        if (queryText.Contains("__schema", StringComparison.Ordinal))
        {
            data["__schema"] = new JsonObject
            {
                ["sdl"] = GetSchemaSdl(),
                ["queryType"] = new JsonObject { ["name"] = "Query" },
                ["mutationType"] = new JsonObject { ["name"] = "Mutation" },
                ["subscriptionType"] = new JsonObject { ["name"] = "Subscription" }
            };
        }

        var isMutation = queryText.StartsWith("mutation", StringComparison.OrdinalIgnoreCase);

        if (isMutation)
        {
            ExecuteMutations(queryText, variables, authToken, data, errors);
        }
        else
        {
            ExecuteQueries(queryText, variables, authToken, data, errors);
        }

        var elapsedUs = GetElapsedMicroseconds(startTick);
        return new GraphQlResponse(
            errors.Count > 0 && data.Count == 0 ? null : data,
            errors,
            elapsedUs);
    }

    /// <summary>
    /// Convenience entry point for executing a raw GraphQL query string against the Rhyolite vault.
    /// </summary>
    public GraphQlResponse ExecuteQuery(string query, string? authToken = null) =>
        Execute(new GraphQlRequest(query), authToken);

    private void ExecuteQueries(
        string queryText,
        JsonObject variables,
        string? authToken,
        JsonObject data,
        List<GraphQlError> errors)
    {
        if (ContainsFieldOperation(queryText, "note"))
        {
            var args = ExtractFieldArguments(queryText, "note", variables);
            var path = GetStringArg(args, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add(new GraphQlError("Field 'note' requires argument 'path'.", "BAD_USER_INPUT"));
            }
            else
            {
                var note = _vaultEngine.GetNote(path);
                var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, path, note?.Tags);
                if (!decision.IsAllowed)
                {
                    errors.Add(new GraphQlError($"RBAC Denied on 'note': {decision.Reason}", "FORBIDDEN"));
                }
                else
                {
                    data["note"] = note is null ? null : MapNoteToGraphQlJson(note);
                }
            }
        }

        if (ContainsFieldOperation(queryText, "notes"))
        {
            var args = ExtractFieldArguments(queryText, "notes", variables);
            var folderPrefix = GetStringArg(args, "folderPrefix");
            var tag = GetStringArg(args, "tag");
            var limit = GetIntArg(args, "limit", 50);

            var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote, folderPrefix);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'notes': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                var normFolder = string.IsNullOrWhiteSpace(folderPrefix) ? null : AccessScope.NormalizeFolderPrefix(folderPrefix);
                var normTag = string.IsNullOrWhiteSpace(tag) ? null : AccessScope.NormalizeTag(tag);

                var notesArray = new JsonArray();
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

                    if (!_authEngine.CheckAccess(authToken, VaultOperation.ReadNote, note.Path, note.Tags).IsAllowed)
                    {
                        continue;
                    }

                    notesArray.Add(MapNoteToGraphQlJson(note));
                    if (notesArray.Count >= limit)
                    {
                        break;
                    }
                }

                data["notes"] = notesArray;
            }
        }

        if (ContainsFieldOperation(queryText, "graph"))
        {
            var args = ExtractFieldArguments(queryText, "graph", variables);
            var rootPath = GetStringArg(args, "rootPath");
            var depth = GetIntArg(args, "depth", 2);

            var decision = _authEngine.CheckAccess(authToken, VaultOperation.QueryGraph, rootPath);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'graph': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                var subgraph = _mcpEngine.BuildGraphSubgraph(rootPath, depth, authToken);
                var nodesArr = new JsonArray();
                foreach (var n in subgraph.Nodes)
                {
                    var tagsArr = new JsonArray();
                    foreach (var t in n.Tags)
                    {
                        tagsArr.Add(t);
                    }

                    nodesArr.Add(new JsonObject
                    {
                        ["path"] = n.Path,
                        ["title"] = n.Title,
                        ["tags"] = tagsArr,
                        ["hopDistance"] = n.HopDistance
                    });
                }

                var edgesArr = new JsonArray();
                foreach (var e in subgraph.Edges)
                {
                    edgesArr.Add(new JsonObject
                    {
                        ["sourcePath"] = e.SourcePath,
                        ["targetPath"] = e.TargetPath
                    });
                }

                data["graph"] = new JsonObject
                {
                    ["rootPath"] = subgraph.RootPath,
                    ["depth"] = subgraph.Depth,
                    ["nodes"] = nodesArr,
                    ["edges"] = edgesArr
                };
            }
        }

        if (ContainsFieldOperation(queryText, "search"))
        {
            var args = ExtractFieldArguments(queryText, "search", variables);
            var q = GetStringArg(args, "query") ?? string.Empty;
            var limit = GetIntArg(args, "limit", 20);

            var decision = _authEngine.CheckAccess(authToken, VaultOperation.SearchVault);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'search': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                var hits = _mcpEngine.SearchNotesInternal(q, null, null, limit, authToken);
                var hitsArr = new JsonArray();
                foreach (var h in hits)
                {
                    var tagsArr = new JsonArray();
                    foreach (var t in h.Tags)
                    {
                        tagsArr.Add(t);
                    }

                    hitsArr.Add(new JsonObject
                    {
                        ["path"] = h.Path,
                        ["title"] = h.Title,
                        ["score"] = h.Score,
                        ["snippet"] = h.Snippet,
                        ["tags"] = tagsArr
                    });
                }

                data["search"] = hitsArr;
            }
        }

        if (ContainsFieldOperation(queryText, "peers"))
        {
            var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'peers': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                var peersArr = new JsonArray();
                foreach (var p in _vaultEngine.GetPeers())
                {
                    peersArr.Add(MapPeerToGraphQlJson(p));
                }
                data["peers"] = peersArr;
            }
        }

        if (ContainsFieldOperation(queryText, "authProviders"))
        {
            var providersArr = new JsonArray();
            foreach (var provider in _authEngine.GetIdentityProviders())
            {
                providersArr.Add(new JsonObject
                {
                    ["providerType"] = provider.ProviderType.ToString(),
                    ["displayName"] = provider.DisplayName,
                    ["authorizationEndpoint"] = provider.AuthorizationEndpoint,
                    ["tokenEndpoint"] = provider.TokenEndpoint,
                    ["supportsPkce"] = provider.SupportsPkce,
                    ["enabled"] = provider.IsEnabled
                });
            }
            data["authProviders"] = providersArr;
        }

        if (ContainsFieldOperation(queryText, "devExPrPreview"))
        {
            var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'devExPrPreview': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                var args = ExtractFieldArguments(queryText, "devExPrPreview", variables);
                var samplePr = EnterpriseDevExEngine.CreateSampleEnterprisePullRequest();
                var prNumber = GetIntArg(args, "prNumber", samplePr.Number);
                if (prNumber != samplePr.Number)
                {
                    samplePr = samplePr with { Number = prNumber };
                }

                var walCacheKey = EnterpriseDevExEngine.ComputePrSqliteWalCacheKey(samplePr.Repository, samplePr.Number, samplePr.HeadSha);
                var singleRttQuery = EnterpriseDevExEngine.BuildSingleRttPullRequestGraphQlQuery("dustinblad", "rhyolite", samplePr.Number);
                var analysis = EnterpriseDevExEngine.AnalyzeGitHubPullRequest(samplePr, []);

                data["devExPrPreview"] = new JsonObject
                {
                    ["repository"] = samplePr.Repository,
                    ["prNumber"] = samplePr.Number,
                    ["number"] = samplePr.Number,
                    ["title"] = samplePr.Title,
                    ["authorLogin"] = samplePr.AuthorLogin,
                    ["headBranch"] = samplePr.HeadBranch,
                    ["baseBranch"] = samplePr.BaseBranch,
                    ["headSha"] = samplePr.HeadSha,
                    ["state"] = samplePr.State,
                    ["ciCheckStatus"] = samplePr.CiCheckStatus,
                    ["reviewDecision"] = samplePr.ReviewDecision,
                    ["riskLevel"] = analysis.RiskLevel,
                    ["unresolvedThreadsCount"] = analysis.UnresolvedThreadsCount,
                    ["actionableBlockersCount"] = analysis.ActionableBlockersCount,
                    ["totalAdditions"] = analysis.TotalAdditions,
                    ["totalDeletions"] = analysis.TotalDeletions,
                    ["sqliteWalCacheKey"] = walCacheKey,
                    ["walCacheKey"] = walCacheKey,
                    ["singleRttGraphQlQuery"] = singleRttQuery,
                    ["generatedReviewMarkdown"] = analysis.GeneratedReviewMarkdown
                };
            }
        }

        if (ContainsFieldOperation(queryText, "workspaceConnectors"))
        {
            var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'workspaceConnectors': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                data["workspaceConnectors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["connectorId"] = "bladbase-primary",
                        ["provider"] = "Bladbase",
                        ["workspace"] = "dustinblad/bladbase",
                        ["workspaceOrSpace"] = "dustinblad/bladbase",
                        ["endpoint"] = "https://bladbase.dev/api/v1/workspaces/dustinblad/bladbase",
                        ["protocol"] = "MCP + GraphQL + HLC Delta Sync",
                        ["status"] = "Active",
                        ["enabled"] = true
                    },
                    new JsonObject
                    {
                        ["connectorId"] = "docmost-primary",
                        ["provider"] = "Docmost",
                        ["space"] = "Engineering Architecture",
                        ["workspaceOrSpace"] = "Engineering Architecture",
                        ["endpoint"] = "https://docs.internal.rhyolite.dev/api",
                        ["protocol"] = "ProseMirror JSON <-> CommonMark + WikiLinks Bidirectional Bridge",
                        ["status"] = "Active",
                        ["enabled"] = true
                    }
                };
            }
        }

        if (queryText.Contains("designSystems", StringComparison.OrdinalIgnoreCase))
        {
            var isAllowed = true;
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                var decision = _authEngine.CheckAccess(authToken, VaultOperation.ReadNote);
                if (!decision.IsAllowed)
                {
                    errors.Add(new GraphQlError($"RBAC Denied on 'designSystems': {decision.Reason}", "FORBIDDEN"));
                    isAllowed = false;
                }
            }

            if (isAllowed)
            {
                var activeId = _vault?.GetUiPreference("active_design_system")
                    ?? _mcpEngine.ActiveDesignSystemId
                    ?? DesignSystemCatalog.DefaultPresetId;

                var presetsArr = new JsonArray();
                foreach (var preset in DesignSystemCatalog.All)
                {
                    presetsArr.Add(new JsonObject
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
                        ["darkAccent"] = preset.DarkAccent,
                        ["isActive"] = string.Equals(preset.Id, activeId, StringComparison.OrdinalIgnoreCase)
                    });
                }

                data["activeDesignSystem"] = activeId;
                data["active_design_system"] = activeId;
                data["designSystems"] = presetsArr;
            }
        }
    }

    private void ExecuteMutations(
        string queryText,
        JsonObject variables,
        string? authToken,
        JsonObject data,
        List<GraphQlError> errors)
    {
        if (ContainsFieldOperation(queryText, "upsertNote"))
        {
            var args = ExtractFieldArguments(queryText, "upsertNote", variables);
            var path = GetStringArg(args, "path");
            var content = GetStringArg(args, "content");
            var tags = GetStringArrayArg(args, "tags");

            if (string.IsNullOrWhiteSpace(path) || content is null)
            {
                errors.Add(new GraphQlError("Mutation 'upsertNote' requires 'path' and 'content'.", "BAD_USER_INPUT"));
            }
            else
            {
                var preview = VaultNoteSnapshot.FromMarkdown(path, content, _vaultEngine.CurrentHlc, tags);
                var decision = _authEngine.CheckAccess(authToken, VaultOperation.WriteNote, preview.Path, preview.Tags);
                if (!decision.IsAllowed)
                {
                    errors.Add(new GraphQlError($"RBAC Denied on 'upsertNote': {decision.Reason}", "FORBIDDEN"));
                }
                else
                {
                    var saved = _vaultEngine.UpsertNote(path, content, tags);
                    data["upsertNote"] = MapNoteToGraphQlJson(saved);
                }
            }
        }

        if (ContainsFieldOperation(queryText, "patchNote"))
        {
            var args = ExtractFieldArguments(queryText, "patchNote", variables);
            var path = GetStringArg(args, "path");
            var heading = GetStringArg(args, "heading");
            var content = GetStringArg(args, "content");

            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(heading) || content is null)
            {
                errors.Add(new GraphQlError("Mutation 'patchNote' requires 'path', 'heading', and 'content'.", "BAD_USER_INPUT"));
            }
            else
            {
                var existing = _vaultEngine.GetNote(path);
                var decision = _authEngine.CheckAccess(authToken, VaultOperation.PatchNote, path, existing?.Tags);
                if (!decision.IsAllowed)
                {
                    errors.Add(new GraphQlError($"RBAC Denied on 'patchNote': {decision.Reason}", "FORBIDDEN"));
                }
                else
                {
                    var patched = _vaultEngine.PatchNoteSection(path, heading, content, createIfMissing: true);
                    data["patchNote"] = MapNoteToGraphQlJson(patched);
                }
            }
        }

        if (ContainsFieldOperation(queryText, "deleteNote"))
        {
            var args = ExtractFieldArguments(queryText, "deleteNote", variables);
            var path = GetStringArg(args, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add(new GraphQlError("Mutation 'deleteNote' requires 'path'.", "BAD_USER_INPUT"));
            }
            else
            {
                var existing = _vaultEngine.GetNote(path);
                var decision = _authEngine.CheckAccess(authToken, VaultOperation.DeleteNote, path, existing?.Tags);
                if (!decision.IsAllowed)
                {
                    errors.Add(new GraphQlError($"RBAC Denied on 'deleteNote': {decision.Reason}", "FORBIDDEN"));
                }
                else
                {
                    data["deleteNote"] = _vaultEngine.DeleteNote(path);
                }
            }
        }

        if (ContainsFieldOperation(queryText, "createApiKey"))
        {
            var args = ExtractFieldArguments(queryText, "createApiKey", variables);
            var label = GetStringArg(args, "label") ?? "GraphQL Issued Key";
            var roleStr = GetStringArg(args, "role") ?? nameof(VaultRole.AiAgentReadWrite);
            var allowedFolders = GetStringArrayArg(args, "allowedFolders");
            var deniedTags = GetStringArrayArg(args, "deniedTags");

            var decision = _authEngine.CheckAccess(authToken, VaultOperation.ManageAuth);
            if (!decision.IsAllowed)
            {
                errors.Add(new GraphQlError($"RBAC Denied on 'createApiKey': {decision.Reason}", "FORBIDDEN"));
            }
            else
            {
                if (!Enum.TryParse<VaultRole>(roleStr, ignoreCase: true, out var parsedRole))
                {
                    parsedRole = VaultRole.AiAgentReadWrite;
                }

                var scope = AccessScope.ForAiAgent(allowedFolders, deniedTags: deniedTags);
                var created = _authEngine.CreateApiKey(decision.PrincipalId, label, parsedRole, scope);
                data["createApiKey"] = new JsonObject
                {
                    ["keyId"] = created.Credential.KeyId,
                    ["label"] = created.Credential.Label,
                    ["role"] = created.Credential.Role.ToString(),
                    ["rawApiKey"] = created.RawApiKey,
                    ["expiresAtUtc"] = created.Credential.ExpiresAtUtc.ToString("O")
                };
            }
        }

        if (ContainsFieldOperation(queryText, "connectPeer"))
        {
            var args = ExtractFieldArguments(queryText, "connectPeer", variables);
            var nodeId = GetStringArg(args, "nodeId");
            var displayName = GetStringArg(args, "displayName") ?? nodeId ?? "Peer Node";
            var endpoint = GetStringArg(args, "endpoint");

            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(endpoint))
            {
                errors.Add(new GraphQlError("Mutation 'connectPeer' requires 'nodeId' and 'endpoint'.", "BAD_USER_INPUT"));
            }
            else
            {
                var decision = _authEngine.CheckAccess(authToken, VaultOperation.SyncPeer);
                if (!decision.IsAllowed)
                {
                    errors.Add(new GraphQlError($"RBAC Denied on 'connectPeer': {decision.Reason}", "FORBIDDEN"));
                }
                else
                {
                    var peer = _vaultEngine.RegisterOrUpdatePeer(nodeId, displayName, endpoint);
                    data["connectPeer"] = MapPeerToGraphQlJson(peer);
                }
            }
        }
    }

    private JsonObject MapNoteToGraphQlJson(VaultNoteSnapshot note)
    {
        var tagsArr = new JsonArray();
        foreach (var t in note.Tags)
        {
            tagsArr.Add(t);
        }

        var fmArr = new JsonArray();
        foreach (var (k, v) in note.Frontmatter)
        {
            fmArr.Add(new JsonObject
            {
                ["key"] = k,
                ["value"] = v
            });
        }

        var outgoingArr = new JsonArray();
        foreach (var link in note.OutgoingLinks)
        {
            outgoingArr.Add(link);
        }

        var backlinksArr = new JsonArray();
        foreach (var backlink in _vaultEngine.GetBacklinks(note.Path))
        {
            backlinksArr.Add(backlink);
        }

        return new JsonObject
        {
            ["path"] = note.Path,
            ["title"] = note.Title,
            ["content"] = note.Content,
            ["sha256"] = note.Sha256,
            ["hlcTimestamp"] = note.Hlc.ToString(),
            ["tags"] = tagsArr,
            ["frontmatter"] = fmArr,
            ["outgoingLinks"] = outgoingArr,
            ["backlinks"] = backlinksArr
        };
    }

    private static JsonObject MapPeerToGraphQlJson(PeerNode peer) => new()
    {
        ["nodeId"] = peer.NodeId,
        ["displayName"] = peer.DisplayName,
        ["endpoint"] = peer.Endpoint,
        ["lastKnownHlc"] = peer.LastKnownHlc.ToString(),
        ["lastRootMerkleHash"] = peer.LastRootMerkleHash,
        ["status"] = peer.Status.ToString()
    };

    private static bool ContainsFieldOperation(string queryText, string fieldName)
    {
        var pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(fieldName)}\s*(\(|\{{|$|\s)";
        var firstBrace = queryText.IndexOf('{');
        var body = firstBrace >= 0 ? queryText[(firstBrace + 1)..] : queryText;
        return Regex.IsMatch(body, pattern);
    }

    private static Dictionary<string, JsonNode?> ExtractFieldArguments(
        string queryText,
        string fieldName,
        JsonObject variables)
    {
        var dict = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (k, v) in variables)
        {
            dict[k] = v?.DeepClone();
        }

        var firstBrace = queryText.IndexOf('{');
        var searchArea = firstBrace >= 0 ? queryText[(firstBrace + 1)..] : queryText;
        var fieldMatch = Regex.Match(searchArea, $@"(?<![A-Za-z0-9_]){Regex.Escape(fieldName)}\s*\(([^)]*)\)");
        if (!fieldMatch.Success)
        {
            return dict;
        }

        var argsSegment = fieldMatch.Groups[1].Value;
        foreach (Match argMatch in ArgumentPairRegex().Matches(argsSegment))
        {
            var argName = argMatch.Groups[1].Value.Trim();
            var rawVal = argMatch.Groups[2].Value.Trim();

            if (rawVal.StartsWith('$'))
            {
                var varName = rawVal[1..].Trim();
                if (variables.TryGetPropertyValue(varName, out var varNode))
                {
                    dict[argName] = varNode?.DeepClone();
                }
            }
            else if (rawVal.StartsWith('"') && rawVal.EndsWith('"') && rawVal.Length >= 2)
            {
                var unescaped = Regex.Unescape(rawVal[1..^1]);
                dict[argName] = JsonValue.Create(unescaped);
            }
            else if (rawVal.StartsWith('[') && rawVal.EndsWith(']'))
            {
                try
                {
                    dict[argName] = JsonNode.Parse(rawVal);
                }
                catch
                {
                    // Ignore malformed array literal
                }
            }
            else if (int.TryParse(rawVal, out var intVal))
            {
                dict[argName] = JsonValue.Create(intVal);
            }
            else if (bool.TryParse(rawVal, out var boolVal))
            {
                dict[argName] = JsonValue.Create(boolVal);
            }
            else
            {
                dict[argName] = JsonValue.Create(rawVal);
            }
        }

        return dict;
    }

    private static string? GetStringArg(IReadOnlyDictionary<string, JsonNode?> args, string name) =>
        args.TryGetValue(name, out var node) && node is not null ? node.GetValue<string>() : null;

    private static int GetIntArg(IReadOnlyDictionary<string, JsonNode?> args, string name, int defaultValue) =>
        args.TryGetValue(name, out var node) && node is not null && int.TryParse(node.ToString(), out var parsed)
            ? parsed
            : defaultValue;

    private static List<string> GetStringArrayArg(IReadOnlyDictionary<string, JsonNode?> args, string name)
    {
        if (!args.TryGetValue(name, out var node) || node is not JsonArray arr)
        {
            return [];
        }

        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var str = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(str))
            {
                list.Add(str);
            }
        }
        return list;
    }

    private static long GetElapsedMicroseconds(long startTick) =>
        Math.Max(1L, (long)Stopwatch.GetElapsedTime(startTick).TotalMicroseconds);

    private static string SerializeGraphQlResponse(GraphQlResponse response)
    {
        var root = new JsonObject
        {
            ["data"] = response.Data?.DeepClone()
        };

        if (response.Errors.Count > 0)
        {
            var errArr = new JsonArray();
            foreach (var err in response.Errors)
            {
                errArr.Add(new JsonObject
                {
                    ["message"] = err.Message,
                    ["extensions"] = new JsonObject { ["code"] = err.Code }
                });
            }
            root["errors"] = errArr;
        }

        root["extensions"] = new JsonObject
        {
            ["executionMicroseconds"] = response.ExecutionMicroseconds
        };

        return root.ToJsonString(JsonOptions);
    }

    [GeneratedRegex(@"([A-Za-z0-9_]+)\s*:\s*(\$[A-Za-z0-9_]+|""(?:\\.|[^""\\])*""|\[[^\]]*\]|[A-Za-z0-9_\-.]+)", RegexOptions.Compiled)]
    private static partial Regex ArgumentPairRegex();
}
