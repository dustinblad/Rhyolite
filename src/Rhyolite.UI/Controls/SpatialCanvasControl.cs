using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rhyolite.Core.Canvas;
using Rhyolite.Core.Design;

namespace Rhyolite.Controls;

/// <summary>
/// Interactive JSON Canvas 1.0 (.canvas) spatial board renderer with draggable cards,
/// group frames, multi-design system palettes, scroll-wheel zoom, background pan,
/// selection highlighting, and directional cubic Bezier edges with arrowheads.
/// </summary>
public sealed class SpatialCanvasControl : Control
{
    public static readonly StyledProperty<CanvasDocument?> DocumentProperty =
        AvaloniaProperty.Register<SpatialCanvasControl, CanvasDocument?>(nameof(Document));

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<SpatialCanvasControl, bool>(nameof(IsDarkTheme));

    public static readonly StyledProperty<string> DesignPresetIdProperty =
        AvaloniaProperty.Register<SpatialCanvasControl, string>(nameof(DesignPresetId), "apple");

    public static readonly StyledProperty<string?> SelectedCardIdProperty =
        AvaloniaProperty.Register<SpatialCanvasControl, string?>(nameof(SelectedCardId));

    private CanvasNode? _draggingNode;
    private Point _dragOffset;
    private Point _dragStartNodePos;
    private bool _nodeMovedDuringDrag;

    private double _zoom = 1.0;
    private Vector _panOffset = Vector.Zero;
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;

    // Cached palette brushes/pens/fonts for O(1) per-frame lookup
    private string? _cachedPresetId;
    private bool _cachedDarkTheme;
    private ISolidColorBrush? _cachedBgBrush;
    private ISolidColorBrush? _cachedDotBrush;
    private ISolidColorBrush? _cachedCardBgBrush;
    private ISolidColorBrush? _cachedPillBgBrush;
    private ISolidColorBrush? _cachedGroupBgBrush;
    private IPen? _cachedBorderPen;
    private IPen? _cachedSelectedBorderPen;
    private ISolidColorBrush? _cachedTitleBrush;
    private ISolidColorBrush? _cachedBodyBrush;
    private ISolidColorBrush? _cachedAccentTextBrush;
    private FontFamily _cachedUiFont = FontFamily.Default;
    private FontFamily _cachedDisplayFont = FontFamily.Default;
    private FontFamily _cachedMonoFont = FontFamily.Default;
    private Typeface _cachedTitleTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private Typeface _cachedBodyTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
    private Typeface _cachedMonoTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private readonly Dictionary<string, (ISolidColorBrush Brush, IPen Pen)> _colorCache = new(StringComparer.OrdinalIgnoreCase);

    public event Action<CanvasNode>? CardClicked;
    public event EventHandler<CanvasDocument>? CanvasMutated;

    static SpatialCanvasControl()
    {
        AffectsRender<SpatialCanvasControl>(
            DocumentProperty,
            IsDarkThemeProperty,
            DesignPresetIdProperty,
            SelectedCardIdProperty);
    }

    public SpatialCanvasControl()
    {
        ClipToBounds = true;
    }

    public CanvasDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
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

    public string? SelectedCardId
    {
        get => GetValue(SelectedCardIdProperty);
        set => SetValue(SelectedCardIdProperty, value);
    }

    public void ResetViewport()
    {
        _zoom = 1.0;
        _panOffset = Vector.Zero;
        InvalidateVisual();
    }

    private Point ScreenToWorld(Point screenPos) =>
        new((screenPos.X - _panOffset.X) / _zoom, (screenPos.Y - _panOffset.Y) / _zoom);

    private static CanvasNode? HitTestNode(CanvasDocument doc, Point worldPos)
    {
        // Hit-test foreground non-group cards first (reverse order for top-most z-order)
        for (int i = doc.Nodes.Count - 1; i >= 0; i--)
        {
            var n = doc.Nodes[i];
            if (string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase))
                continue;

            if (new Rect(n.X, n.Y, n.Width, n.Height).Contains(worldPos))
            {
                return n;
            }
        }

        // Fallback to group frames so users can select and drag groups as well
        for (int i = doc.Nodes.Count - 1; i >= 0; i--)
        {
            var n = doc.Nodes[i];
            if (!string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase))
                continue;

            if (new Rect(n.X, n.Y, n.Width, n.Height).Contains(worldPos))
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
        _zoom = Math.Clamp(_zoom * factor, 0.5, 2.5);
        _panOffset = new Vector(
            pointerPos.X - (worldBefore.X * _zoom),
            pointerPos.Y - (worldBefore.Y * _zoom));

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var doc = Document;

        if (doc is not null)
        {
            var worldPos = ScreenToWorld(pos);
            var hit = HitTestNode(doc, worldPos);

            if (hit is not null)
            {
                _draggingNode = hit;
                _dragOffset = new Point(worldPos.X - hit.X, worldPos.Y - hit.Y);
                _dragStartNodePos = new Point(hit.X, hit.Y);
                _nodeMovedDuringDrag = false;

                SetCurrentValue(SelectedCardIdProperty, hit.Id);
                CardClicked?.Invoke(hit);
                InvalidateVisual();
                return;
            }
        }

        _isPanning = true;
        _panStartPointer = pos;
        _panStartOffset = _panOffset;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_draggingNode is not null)
        {
            var worldPos = ScreenToWorld(pos);
            double nextX = Math.Round(worldPos.X - _dragOffset.X, 1);
            double nextY = Math.Round(worldPos.Y - _dragOffset.Y, 1);

            if (Math.Abs(nextX - _dragStartNodePos.X) > 0.5 ||
                Math.Abs(nextY - _dragStartNodePos.Y) > 0.5)
            {
                _nodeMovedDuringDrag = true;
            }

            _draggingNode.X = nextX;
            _draggingNode.Y = nextY;
            InvalidateVisual();
            return;
        }

        if (_isPanning)
        {
            _panOffset = _panStartOffset + (pos - _panStartPointer);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_draggingNode is not null)
        {
            bool moved = _nodeMovedDuringDrag;
            _draggingNode = null;
            _nodeMovedDuringDrag = false;

            if (moved && Document is { } doc)
            {
                CanvasMutated?.Invoke(this, doc);
            }
        }

        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
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
        double cardBorderWidth = ParseBorderThickness(preset.CardBorderThickness, 1.2);

        _cachedPresetId = preset.Id;
        _cachedDarkTheme = dark;
        _cachedBgBrush = new SolidColorBrush(Color.Parse(palette.BgHex));
        _cachedDotBrush = new SolidColorBrush(Color.Parse(palette.BorderSoftHex));
        _cachedCardBgBrush = new SolidColorBrush(Color.Parse(palette.SurfaceHex));
        _cachedPillBgBrush = new SolidColorBrush(Color.Parse(palette.SurfaceElevatedHex));
        _cachedGroupBgBrush = new SolidColorBrush(Color.Parse(palette.CalloutBgHex));
        _cachedBorderPen = new Pen(new SolidColorBrush(Color.Parse(palette.BorderHex)), cardBorderWidth);
        _cachedSelectedBorderPen = new Pen(new SolidColorBrush(Color.Parse(palette.AccentHex)), Math.Max(2.5, cardBorderWidth + 1.0));
        _cachedTitleBrush = new SolidColorBrush(Color.Parse(palette.FgHex));
        _cachedBodyBrush = new SolidColorBrush(Color.Parse(palette.MutedHex));
        _cachedAccentTextBrush = new SolidColorBrush(Color.Parse(palette.LinkHex));

        _cachedUiFont = string.IsNullOrWhiteSpace(preset.UiFontFamily)
            ? FontFamily.Default
            : new FontFamily(preset.UiFontFamily);
        _cachedDisplayFont = string.IsNullOrWhiteSpace(preset.DisplayFontFamily)
            ? _cachedUiFont
            : new FontFamily(preset.DisplayFontFamily);
        _cachedMonoFont = string.IsNullOrWhiteSpace(preset.MonoFontFamily)
            ? FontFamily.Default
            : new FontFamily(preset.MonoFontFamily);

        _cachedTitleTypeface = new Typeface(_cachedDisplayFont, FontStyle.Normal, FontWeight.SemiBold);
        _cachedBodyTypeface = new Typeface(_cachedUiFont, FontStyle.Normal, FontWeight.Normal);
        _cachedMonoTypeface = new Typeface(_cachedMonoFont, FontStyle.Normal, FontWeight.SemiBold);
    }

    private (ISolidColorBrush Brush, IPen Pen) GetColorStyle(string? colorHex, string fallbackAccentHex)
    {
        string key = string.IsNullOrWhiteSpace(colorHex) ? fallbackAccentHex : colorHex;
        if (!_colorCache.TryGetValue(key, out var entry))
        {
            if (!Color.TryParse(key, out var parsed))
            {
                parsed = Color.Parse(fallbackAccentHex);
            }
            var brush = new SolidColorBrush(parsed);
            var pen = new Pen(brush, 2.2);
            entry = (brush, pen);
            _colorCache[key] = entry;
        }
        return entry;
    }

    private static Point GetNodeSideAnchor(CanvasNode node, string? side, bool isFrom)
    {
        string normalized = string.IsNullOrWhiteSpace(side)
            ? (isFrom ? "right" : "left")
            : side.Trim().ToLowerInvariant();

        return normalized switch
        {
            "top" => new Point(node.X + (node.Width / 2.0), node.Y),
            "bottom" => new Point(node.X + (node.Width / 2.0), node.Y + node.Height),
            "left" => new Point(node.X, node.Y + (node.Height / 2.0)),
            _ => new Point(node.X + node.Width, node.Y + (node.Height / 2.0))
        };
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

        double cardCornerRadius = Math.Max(0.0, preset.CardCornerRadius);
        double pillCornerRadius = Math.Clamp(preset.PillCornerRadius, 0.0, 12.0);
        double groupCornerRadius = cardCornerRadius <= 3.0 ? cardCornerRadius : cardCornerRadius + 4.0;

        context.DrawRectangle(
            _cachedBgBrush!,
            _cachedBorderPen,
            new RoundedRect(new Rect(0, 0, bounds.Width, bounds.Height), Math.Clamp(cardCornerRadius, 0.0, 18.0)));

        double gridStep = Math.Max(12.0, 20.0 * _zoom);
        double offsetX = ((_panOffset.X % gridStep) + gridStep) % gridStep;
        double offsetY = ((_panOffset.Y % gridStep) + gridStep) % gridStep;

        for (double gx = offsetX; gx < bounds.Width; gx += gridStep)
        {
            for (double gy = offsetY; gy < bounds.Height; gy += gridStep)
            {
                context.DrawEllipse(_cachedDotBrush!, null, new Point(gx, gy), 1.1, 1.1);
            }
        }

        var doc = Document;
        if (doc is null) return;

        var nodeMap = new Dictionary<string, CanvasNode>(doc.Nodes.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < doc.Nodes.Count; i++)
        {
            var n = doc.Nodes[i];
            nodeMap[n.Id] = n;
        }

        string? selectedId = SelectedCardId;

        var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y);
        using (context.PushTransform(transform))
        {
            // 1. Render Groups
            for (int i = 0; i < doc.Nodes.Count; i++)
            {
                var group = doc.Nodes[i];
                if (!string.Equals(group.Type, "group", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isSelected = !string.IsNullOrEmpty(selectedId) &&
                    string.Equals(group.Id, selectedId, StringComparison.OrdinalIgnoreCase);

                var rect = new Rect(group.X, group.Y, group.Width, group.Height);
                context.DrawRectangle(
                    _cachedGroupBgBrush,
                    isSelected ? _cachedSelectedBorderPen : _cachedBorderPen,
                    new RoundedRect(rect, groupCornerRadius));

                if (!string.IsNullOrWhiteSpace(group.Label))
                {
                    var labelText = new FormattedText(
                        group.Label.ToUpperInvariant(),
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _cachedMonoTypeface,
                        11.5,
                        _cachedBodyBrush!);
                    context.DrawText(labelText, new Point(group.X + 16, group.Y + 12));
                }
            }

            // 2. Render Smooth Cubic Bezier Edges + Arrowheads
            for (int i = 0; i < doc.Edges.Count; i++)
            {
                var edge = doc.Edges[i];
                if (!nodeMap.TryGetValue(edge.FromNode, out var from) || !nodeMap.TryGetValue(edge.ToNode, out var to))
                    continue;

                var start = GetNodeSideAnchor(from, edge.FromSide, isFrom: true);
                var end = GetNodeSideAnchor(to, edge.ToSide, isFrom: false);
                var (edgeBrush, edgePen) = GetColorStyle(edge.Color, palette.AccentHex);

                double dx = Math.Max(48.0, Math.Abs(end.X - start.X) * 0.45);
                var cp1 = new Point(start.X + dx, start.Y);
                var cp2 = new Point(end.X - dx, end.Y);

                var bezierGeometry = new StreamGeometry();
                using (var geoCtx = bezierGeometry.Open())
                {
                    geoCtx.BeginFigure(start, isFilled: false);
                    geoCtx.CubicBezierTo(cp1, cp2, end);
                    geoCtx.EndFigure(isClosed: false);
                }
                context.DrawGeometry(null, edgePen, bezierGeometry);

                // Arrowhead triangle at target card
                double tx = end.X - cp2.X;
                double ty = end.Y - cp2.Y;
                double tLen = Math.Sqrt((tx * tx) + (ty * ty));
                if (tLen < 0.001)
                {
                    tx = end.X - start.X;
                    ty = end.Y - start.Y;
                    tLen = Math.Max(1.0, Math.Sqrt((tx * tx) + (ty * ty)));
                }

                double ux = tx / tLen;
                double uy = ty / tLen;
                const double arrowLen = 9.5;
                const double arrowWing = 4.6;

                var wingLeft = new Point(
                    end.X - (ux * arrowLen) - (uy * arrowWing),
                    end.Y - (uy * arrowLen) + (ux * arrowWing));
                var wingRight = new Point(
                    end.X - (ux * arrowLen) + (uy * arrowWing),
                    end.Y - (uy * arrowLen) - (ux * arrowWing));

                var arrowGeometry = new StreamGeometry();
                using (var arrowCtx = arrowGeometry.Open())
                {
                    arrowCtx.BeginFigure(end, isFilled: true);
                    arrowCtx.LineTo(wingLeft);
                    arrowCtx.LineTo(wingRight);
                    arrowCtx.EndFigure(isClosed: true);
                }
                context.DrawGeometry(edgeBrush, null, arrowGeometry);

                if (!string.IsNullOrWhiteSpace(edge.Label))
                {
                    double midX = (0.125 * start.X) + (0.375 * cp1.X) + (0.375 * cp2.X) + (0.125 * end.X);
                    double midY = (0.125 * start.Y) + (0.375 * cp1.Y) + (0.375 * cp2.Y) + (0.125 * end.Y);
                    var edgeLabel = new FormattedText(
                        edge.Label,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _cachedMonoTypeface,
                        10.5,
                        _cachedBodyBrush!);

                    var labelPillRect = new Rect(
                        midX - (edgeLabel.Width / 2.0) - 6.0,
                        midY - 20.0,
                        edgeLabel.Width + 12.0,
                        edgeLabel.Height + 4.0);
                    context.DrawRectangle(_cachedPillBgBrush, _cachedBorderPen, new RoundedRect(labelPillRect, pillCornerRadius));
                    context.DrawText(edgeLabel, new Point(midX - (edgeLabel.Width / 2.0), midY - 18.0));
                }
            }

            // 3. Render Cards
            for (int i = 0; i < doc.Nodes.Count; i++)
            {
                var card = doc.Nodes[i];
                if (string.Equals(card.Type, "group", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isSelected = !string.IsNullOrEmpty(selectedId) &&
                    string.Equals(card.Id, selectedId, StringComparison.OrdinalIgnoreCase);

                var rect = new Rect(card.X, card.Y, card.Width, card.Height);
                context.DrawRectangle(
                    _cachedCardBgBrush,
                    isSelected ? _cachedSelectedBorderPen : _cachedBorderPen,
                    new RoundedRect(rect, cardCornerRadius));

                // Top accent strip
                var (accentBrush, _) = GetColorStyle(card.Color, palette.AccentHex);
                context.FillRectangle(accentBrush, new Rect(card.X + 12, card.Y + 8, Math.Max(16, card.Width - 24), 3.5));

                // Category Pill Badge inside card (top-right) using preset.PillCornerRadius & MonoFont
                string badgeLabel = string.Equals(card.Type, "file", StringComparison.OrdinalIgnoreCase)
                    ? "FILE"
                    : string.Equals(card.Type, "link", StringComparison.OrdinalIgnoreCase)
                        ? "LINK"
                        : "SPEC";
                var badgeText = new FormattedText(
                    badgeLabel,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _cachedMonoTypeface,
                    9.5,
                    _cachedAccentTextBrush!);
                double badgeW = badgeText.Width + 10.0;
                double badgeH = badgeText.Height + 4.0;
                var badgeRect = new Rect(card.X + card.Width - badgeW - 10.0, card.Y + 16.0, badgeW, badgeH);
                context.DrawRectangle(
                    _cachedPillBgBrush,
                    _cachedBorderPen,
                    new RoundedRect(badgeRect, pillCornerRadius));
                context.DrawText(badgeText, new Point(badgeRect.X + 5.0, badgeRect.Y + 2.0));

                var title = new FormattedText(
                    card.Label ?? card.File ?? "Canvas Card",
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _cachedTitleTypeface,
                    13,
                    _cachedTitleBrush!)
                {
                    MaxTextWidth = Math.Max(40, card.Width - badgeW - 30)
                };
                context.DrawText(title, new Point(card.X + 12, card.Y + 18));

                if (!string.IsNullOrWhiteSpace(card.Text))
                {
                    var body = new FormattedText(
                        card.Text,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _cachedBodyTypeface,
                        12,
                        _cachedBodyBrush!)
                    {
                        MaxTextWidth = Math.Max(40, card.Width - 24),
                        MaxTextHeight = Math.Max(20, card.Height - 52)
                    };
                    context.DrawText(body, new Point(card.X + 12, card.Y + 46));
                }
            }
        }
    }
}
