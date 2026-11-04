using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rhyolite.Core.Design;
using Rhyolite.Core.Graph;

namespace Rhyolite.Controls;

/// <summary>
/// Hardware-accelerated Skia interactive 2D force-directed Knowledge Graph control.
/// Supports multi-design system palettes, scroll-wheel zoom, background pan, node dragging,
/// neighborhood hover spotlighting, click selection, and double-click note activation.
/// </summary>
public sealed class KnowledgeGraphControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphNode2D>?> NodesProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, IReadOnlyList<GraphNode2D>?>(nameof(Nodes));

    public static readonly StyledProperty<IReadOnlyList<GraphEdge2D>?> EdgesProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, IReadOnlyList<GraphEdge2D>?>(nameof(Edges));

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, bool>(nameof(IsDarkTheme));

    public static readonly StyledProperty<string> DesignPresetIdProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, string>(nameof(DesignPresetId), "apple");

    private GraphNode2D? _hoveredNode;
    private GraphNode2D? _draggingNode;
    private GraphNode2D? _selectedNode;
    private GraphNode2D? _lastClickedNode;
    private long _lastClickTimestampMs;

    private double _zoom = 1.0;
    private Vector _panOffset = Vector.Zero;
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;

    // Cached palette brushes/pens/fonts for O(1) per-frame lookup
    private string? _cachedPresetId;
    private bool _cachedDarkTheme;
    private ISolidColorBrush? _cachedBgBrush;
    private ISolidColorBrush? _cachedSurfaceBrush;
    private ISolidColorBrush? _cachedDotBrush;
    private IPen? _cachedBaseEdgePen;
    private IPen? _cachedActiveEdgePen;
    private IPen? _cachedRingPen;
    private IPen? _cachedFocusPen;
    private IPen? _cachedHudBorderPen;
    private ISolidColorBrush? _cachedTextBrush;
    private ISolidColorBrush? _cachedMutedTextBrush;
    private FontFamily _cachedUiFont = FontFamily.Default;
    private FontFamily _cachedDisplayFont = FontFamily.Default;
    private FontFamily _cachedMonoFont = FontFamily.Default;
    private Typeface _cachedDisplayTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private Typeface _cachedUiTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private Typeface _cachedMonoTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
    private readonly Dictionary<string, ISolidColorBrush> _nodeBrushCache = new(StringComparer.OrdinalIgnoreCase);

    public event Action<GraphNode2D>? NodeSelected;
    public event EventHandler<GraphNode2D>? NodeDoubleClicked;

    static KnowledgeGraphControl()
    {
        AffectsRender<KnowledgeGraphControl>(
            NodesProperty,
            EdgesProperty,
            IsDarkThemeProperty,
            DesignPresetIdProperty);
    }

    public KnowledgeGraphControl()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<GraphNode2D>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IReadOnlyList<GraphEdge2D>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public bool IsDarkTheme
    {
        get => GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    public string DesignPresetId
    {
        get => GetValue(DesignPresetIdProperty);
        set => SetValue(DesignPresetIdProperty, value);
    }

    public void ResetViewport()
    {
        _zoom = 1.0;
        _panOffset = Vector.Zero;
        InvalidateVisual();
    }

    private Point ScreenToWorld(Point screenPos) =>
        new((screenPos.X - _panOffset.X) / _zoom, (screenPos.Y - _panOffset.Y) / _zoom);

    private GraphNode2D? HitTestNode(Point worldPos)
    {
        var nodes = Nodes;
        if (nodes is null || nodes.Count == 0) return null;

        double hitPadding = 6.0 / _zoom;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            double dx = n.X - worldPos.X;
            double dy = n.Y - worldPos.Y;
            double maxDist = n.Radius + hitPadding;
            if ((dx * dx) + (dy * dy) <= maxDist * maxDist)
            {
                return n;
            }
        }

        return null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Math.Abs(e.Delta.Y) < 0.001) return;

        var pointerPos = e.GetPosition(this);
        var worldBefore = ScreenToWorld(pointerPos);

        double factor = e.Delta.Y > 0 ? 1.12 : (1.0 / 1.12);
        _zoom = Math.Clamp(_zoom * factor, 0.45, 2.75);
        _panOffset = new Vector(
            pointerPos.X - (worldBefore.X * _zoom),
            pointerPos.Y - (worldBefore.Y * _zoom));

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_draggingNode is not null)
        {
            var worldPos = ScreenToWorld(pos);
            _draggingNode.X = worldPos.X;
            _draggingNode.Y = worldPos.Y;
            InvalidateVisual();
            return;
        }

        if (_isPanning)
        {
            _panOffset = _panStartOffset + (pos - _panStartPointer);
            InvalidateVisual();
            return;
        }

        var hit = HitTestNode(ScreenToWorld(pos));
        if (!ReferenceEquals(hit, _hoveredNode))
        {
            _hoveredNode = hit;
            Cursor = hit is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var hit = HitTestNode(ScreenToWorld(pos));

        if (hit is not null)
        {
            long nowMs = Environment.TickCount64;
            bool isDoubleClick = e.ClickCount >= 2 ||
                (ReferenceEquals(hit, _lastClickedNode) && (nowMs - _lastClickTimestampMs) <= 400);

            _lastClickedNode = hit;
            _lastClickTimestampMs = nowMs;
            _selectedNode = hit;
            _hoveredNode = hit;
            _draggingNode = hit;

            NodeSelected?.Invoke(hit);
            if (isDoubleClick)
            {
                NodeDoubleClicked?.Invoke(this, hit);
            }

            InvalidateVisual();
        }
        else
        {
            _isPanning = true;
            _panStartPointer = pos;
            _panStartOffset = _panOffset;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _draggingNode = null;
        if (_isPanning)
        {
            _isPanning = false;
            Cursor = _hoveredNode is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    private static double ParseBorderThickness(string? raw, double fallback = 1.2)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var token = raw.Split(',')[0].Trim();
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Clamp(parsed, 1.0, 3.0);
            }
        }
        return fallback;
    }

    private void EnsureThemeCache(string presetId, bool dark)
    {
        if (string.Equals(_cachedPresetId, presetId, StringComparison.OrdinalIgnoreCase) &&
            _cachedDarkTheme == dark &&
            _cachedBgBrush is not null)
        {
            return;
        }

        var preset = DesignSystemCatalog.GetOrDefault(presetId);
        var palette = dark ? preset.DarkPalette : preset.LightPalette;
        double borderThickness = ParseBorderThickness(preset.CardBorderThickness, 1.2);

        _cachedPresetId = preset.Id;
        _cachedDarkTheme = dark;
        _cachedBgBrush = new SolidColorBrush(Color.Parse(palette.BgHex));
        _cachedSurfaceBrush = new SolidColorBrush(Color.Parse(palette.SurfaceHex));
        _cachedDotBrush = new SolidColorBrush(Color.Parse(palette.BorderSoftHex));
        _cachedBaseEdgePen = new Pen(new SolidColorBrush(Color.Parse(palette.BorderHex)), Math.Max(1.4, borderThickness));
        _cachedActiveEdgePen = new Pen(new SolidColorBrush(Color.Parse(palette.AccentHex)), 2.6);
        _cachedRingPen = new Pen(new SolidColorBrush(Color.Parse(palette.BgHex)), Math.Max(2.0, borderThickness + 0.8));
        _cachedFocusPen = new Pen(new SolidColorBrush(Color.Parse(palette.LinkHex)), 3.5);
        _cachedHudBorderPen = new Pen(new SolidColorBrush(Color.Parse(palette.BorderHex)), borderThickness);
        _cachedTextBrush = new SolidColorBrush(Color.Parse(palette.FgHex));
        _cachedMutedTextBrush = new SolidColorBrush(Color.Parse(palette.MutedHex));

        _cachedUiFont = string.IsNullOrWhiteSpace(preset.UiFontFamily)
            ? FontFamily.Default
            : new FontFamily(preset.UiFontFamily);
        _cachedDisplayFont = string.IsNullOrWhiteSpace(preset.DisplayFontFamily)
            ? _cachedUiFont
            : new FontFamily(preset.DisplayFontFamily);
        _cachedMonoFont = string.IsNullOrWhiteSpace(preset.MonoFontFamily)
            ? FontFamily.Default
            : new FontFamily(preset.MonoFontFamily);

        _cachedDisplayTypeface = new Typeface(_cachedDisplayFont, FontStyle.Normal, FontWeight.SemiBold);
        _cachedUiTypeface = new Typeface(_cachedUiFont, FontStyle.Normal, FontWeight.SemiBold);
        _cachedMonoTypeface = new Typeface(_cachedMonoFont, FontStyle.Normal, FontWeight.Medium);
    }

    private ISolidColorBrush GetNodeBrush(string colorHex, string fallbackAccentHex)
    {
        string key = string.IsNullOrWhiteSpace(colorHex) ? fallbackAccentHex : colorHex;
        if (!_nodeBrushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.Parse(key));
            _nodeBrushCache[key] = brush;
        }
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        bool dark = IsDarkTheme;
        string presetId = DesignPresetId ?? DesignSystemCatalog.DefaultPresetId;
        EnsureThemeCache(presetId, dark);

        var preset = DesignSystemCatalog.GetOrDefault(presetId);
        var palette = dark ? preset.DarkPalette : preset.LightPalette;

        double frameCornerRadius = (float)Math.Clamp(preset.CardCornerRadius, 0, 18);
        var frameRect = new Rect(0, 0, bounds.Width, bounds.Height);
        context.DrawRectangle(_cachedBgBrush!, _cachedHudBorderPen, new RoundedRect(frameRect, frameCornerRadius));

        // Dot grid scaled and translated smoothly with viewport pan/zoom
        double gridStep = Math.Max(14.0, 24.0 * _zoom);
        double offsetX = ((_panOffset.X % gridStep) + gridStep) % gridStep;
        double offsetY = ((_panOffset.Y % gridStep) + gridStep) % gridStep;

        for (double gx = offsetX; gx < bounds.Width; gx += gridStep)
        {
            for (double gy = offsetY; gy < bounds.Height; gy += gridStep)
            {
                context.DrawEllipse(_cachedDotBrush!, null, new Point(gx, gy), 1.2, 1.2);
            }
        }

        var nodes = Nodes;
        var edges = Edges;
        if (nodes is null || nodes.Count == 0) return;

        var nodeMap = new Dictionary<string, GraphNode2D>(nodes.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            nodeMap[n.Id] = n;
        }

        bool isSquaredGeometry = preset.CardCornerRadius <= 3.0;
        double nodeCornerRadius = Math.Clamp(preset.CardCornerRadius, 0.0, 6.0);

        var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y);
        using (context.PushTransform(transform))
        {
            var focusTarget = _hoveredNode ?? _selectedNode;

            if (edges is not null)
            {
                for (int i = 0; i < edges.Count; i++)
                {
                    var edge = edges[i];
                    if (!nodeMap.TryGetValue(edge.SourceId, out var a) || !nodeMap.TryGetValue(edge.TargetId, out var b))
                        continue;

                    bool highlighted = focusTarget is not null &&
                        (string.Equals(focusTarget.Id, a.Id, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(focusTarget.Id, b.Id, StringComparison.OrdinalIgnoreCase));

                    context.DrawLine(
                        highlighted ? _cachedActiveEdgePen! : _cachedBaseEdgePen!,
                        new Point(a.X, a.Y),
                        new Point(b.X, b.Y));
                }
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                bool isFocused = ReferenceEquals(node, _hoveredNode) || ReferenceEquals(node, _selectedNode);
                double r = ReferenceEquals(node, _hoveredNode) ? node.Radius + 3.0 : node.Radius;
                var fillBrush = GetNodeBrush(node.ColorHex, palette.AccentHex);

                if (isSquaredGeometry)
                {
                    // Vercel / Supabase sharp or micro-radius geometric nodes
                    var nodeRect = new Rect(node.X - r, node.Y - r, r * 2.0, r * 2.0);
                    context.DrawRectangle(
                        fillBrush,
                        isFocused ? _cachedFocusPen : _cachedRingPen,
                        new RoundedRect(nodeRect, nodeCornerRadius));
                }
                else
                {
                    if (preset.CardCornerRadius >= 14.0)
                    {
                        context.DrawEllipse(null, _cachedBaseEdgePen, new Point(node.X, node.Y), r + 3.5, r + 3.5);
                    }

                    context.DrawEllipse(
                        fillBrush,
                        isFocused ? _cachedFocusPen : _cachedRingPen,
                        new Point(node.X, node.Y),
                        r,
                        r);
                }

                var titleFormatted = new FormattedText(
                    node.Title,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _cachedDisplayTypeface,
                    12.5,
                    _cachedTextBrush!);

                context.DrawText(titleFormatted, new Point(node.X - (titleFormatted.Width / 2.0), node.Y + r + 4.0));

                var rankFormatted = new FormattedText(
                    $"PR {node.PageRank:0.00}",
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _cachedMonoTypeface,
                    10.5,
                    _cachedMutedTextBrush!);

                context.DrawText(
                    rankFormatted,
                    new Point(node.X - (rankFormatted.Width / 2.0), node.Y + r + 4.0 + titleFormatted.Height + 1.0));
            }
        }

        // Top-Left Theme & Geometry HUD Badge
        var hudText = new FormattedText(
            $"{preset.Name.ToUpperInvariant()} · {nodes.Count} NODES · {edges?.Count ?? 0} EDGES · {_zoom * 100:0}%",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _cachedMonoTypeface,
            11,
            _cachedMutedTextBrush!);

        var hudRect = new Rect(14, 14, hudText.Width + 20, hudText.Height + 10);
        context.DrawRectangle(
            _cachedSurfaceBrush,
            _cachedHudBorderPen,
            new RoundedRect(hudRect, frameCornerRadius));
        context.DrawText(hudText, new Point(hudRect.X + 10, hudRect.Y + 5));
    }
}
