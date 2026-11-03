using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rhyolite.Core.Canvas;

/// <summary>
/// Open JSON Canvas 1.0 (https://jsoncanvas.org/) compatible model and engine for .canvas spatial workspaces.
/// </summary>
public sealed class CanvasDocument
{
    [JsonPropertyName("nodes")]
    public List<CanvasNode> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<CanvasEdge> Edges { get; set; } = [];
}

public sealed class CanvasNode
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>Node type: "text", "file", "link", or "group".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; } = 260;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 140;

    [JsonPropertyName("color")]
    public string? Color { get; set; } = "#0071e3";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public sealed class CanvasEdge
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("fromNode")]
    public required string FromNode { get; set; }

    [JsonPropertyName("fromSide")]
    public string FromSide { get; set; } = "right";

    [JsonPropertyName("toNode")]
    public required string ToNode { get; set; }

    [JsonPropertyName("toSide")]
    public string ToSide { get; set; } = "left";

    [JsonPropertyName("color")]
    public string? Color { get; set; } = "#0071e3";

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public static class JsonCanvasEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CanvasDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CreateDefaultArchitectureCanvas();

        try
        {
            return JsonSerializer.Deserialize<CanvasDocument>(json, JsonOptions) ?? CreateDefaultArchitectureCanvas();
        }
        catch
        {
            return CreateDefaultArchitectureCanvas();
        }
    }

    public static string Serialize(CanvasDocument doc) =>
        JsonSerializer.Serialize(doc, JsonOptions);

    public static CanvasDocument CreateDefaultArchitectureCanvas()
    {
        return new CanvasDocument
        {
            Nodes =
            [
                new CanvasNode
                {
                    Id = "group-core",
                    Type = "group",
                    Label = "Rhyolite .NET 10 Hybrid Storage & Protocol Architecture",
                    X = 30,
                    Y = 25,
                    Width = 760,
                    Height = 430,
                    Color = "#86868b"
                },
                new CanvasNode
                {
                    Id = "node-md",
                    Type = "file",
                    File = "01 — Architecture/Storage Architecture.md",
                    Label = "Plain .md Source of Truth",
                    Text = "Zero lock-in local Markdown files.\n0.14 ms atomic disk writes.",
                    X = 60,
                    Y = 95,
                    Width = 220,
                    Height = 135,
                    Color = "#0071e3"
                },
                new CanvasNode
                {
                    Id = "node-ast",
                    Type = "text",
                    Label = "In-Memory AST Delta Engine",
                    Text = "Markdig + YAML incremental parser.\nO(1) graph & backlink updates.",
                    X = 330,
                    Y = 65,
                    Width = 220,
                    Height = 125,
                    Color = "#16a34a"
                },
                new CanvasNode
                {
                    Id = "node-sqlite",
                    Type = "text",
                    Label = "SQLite WAL Sidecar (.rhyolite/cache.db)",
                    Text = "Pre-parsed FTS5 tokens, PageRank & FSRS state.\n< 50 ms cold boot on 10k notes.",
                    X = 330,
                    Y = 230,
                    Width = 220,
                    Height = 130,
                    Color = "#eab308"
                },
                new CanvasNode
                {
                    Id = "node-protocol",
                    Type = "file",
                    File = "01 — Architecture/MCP and GraphQL P2P Mesh.md",
                    Label = "MCP + GraphQL + P2P Mesh",
                    Text = "AI Agent JSON-RPC 2.0 + GraphQL + HLC P2P sync.\nEmail/RBAC + Google/GitHub/Apple/SSO.",
                    X = 590,
                    Y = 145,
                    Width = 185,
                    Height = 155,
                    Color = "#2997ff"
                }
            ],
            Edges =
            [
                new CanvasEdge
                {
                    Id = "edge-1",
                    FromNode = "node-md",
                    ToNode = "node-ast",
                    Color = "#0071e3",
                    Label = "150ms debounce"
                },
                new CanvasEdge
                {
                    Id = "edge-2",
                    FromNode = "node-md",
                    ToNode = "node-sqlite",
                    Color = "#eab308",
                    Label = "WAL upsert"
                },
                new CanvasEdge
                {
                    Id = "edge-3",
                    FromNode = "node-ast",
                    ToNode = "node-protocol",
                    Color = "#16a34a",
                    Label = "Sub-ms API"
                },
                new CanvasEdge
                {
                    Id = "edge-4",
                    FromNode = "node-sqlite",
                    ToNode = "node-protocol",
                    Color = "#2997ff",
                    Label = "Merkle P2P"
                }
            ]
        };
    }
}
