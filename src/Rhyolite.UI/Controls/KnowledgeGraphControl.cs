using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rhyolite.Core.Graph;

namespace Rhyolite.Controls;

/// <summary>
/// Hardware-accelerated Skia interactive 2D force-directed Knowledge Graph control.
/// Supports node dragging, neighborhood hover spotlighting, and click selection.
/// </summary>
public sealed class KnowledgeGraphControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphNode2D>?> NodesProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, IReadOnlyList<GraphNode2D>?>(nameof(Nodes));

    public static readonly StyledProperty<IReadOnlyList<GraphEdge2D>?> EdgesProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, IReadOnlyList<GraphEdge2D>?>(nameof(Edges));

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<KnowledgeGraphControl, bool>(nameof(IsDarkTheme));

    private GraphNode2D? _hoveredNode;
    private GraphNode2D? _draggingNode;

    public event Action<GraphNode2D>? NodeSelected;

    static KnowledgeGraphControl()
    {
        AffectsRender<KnowledgeGraphControl>(NodesProperty, EdgesProperty, IsDarkThemeProperty);
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        var nodes = Nodes;
        if (nodes is null || nodes.Count == 0) return;

        if (_draggingNode is not null)
        {
            _draggingNode.X = Math.Clamp(pos.X, 40, Math.Max(80, Bounds.Width - 40));
            _draggingNode.Y = Math.Clamp(pos.Y, 40, Math.Max(80, Bounds.Height - 40));
            InvalidateVisual();
            return;
        }

        var hit = nodes.FirstOrDefault(n =>
            Math.Sqrt(Math.Pow(n.X - pos.X, 2) + Math.Pow(n.Y - pos.Y, 2)) <= n.Radius + 6);

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
        if (_hoveredNode is not null)
        {
            _draggingNode = _hoveredNode;
            NodeSelected?.Invoke(_hoveredNode);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _draggingNode = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        bool dark = IsDarkTheme;
        var bgBrush = new SolidColorBrush(Color.Parse(dark ? "#000000" : "#FFFFFF"));
        context.FillRectangle(bgBrush, new Rect(0, 0, bounds.Width, bounds.Height));

        // Subtle Apple dot grid
        var dotBrush = new SolidColorBrush(Color.Parse(dark ? "#2C2C2E" : "#E8E8ED"));
        for (double gx = 24; gx < bounds.Width; gx += 24)
        {
            for (double gy = 24; gy < bounds.Height; gy += 24)
            {
                context.DrawEllipse(dotBrush, null, new Point(gx, gy), 1.2, 1.2);
            }
        }

        var nodes = Nodes;
        var edges = Edges;
        if (nodes is null || nodes.Count == 0) return;

        var nodeMap = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var defaultEdgePen = new Pen(new SolidColorBrush(Color.Parse(dark ? "#44EBEBF5" : "#331D1D1F")), 1.6);
        var activeEdgePen = new Pen(new SolidColorBrush(Color.Parse("#0071E3")), 2.6);

        if (edges is not null)
        {
            foreach (var edge in edges)
            {
                if (!nodeMap.TryGetValue(edge.SourceId, out var a) || !nodeMap.TryGetValue(edge.TargetId, out var b))
                    continue;

                bool highlighted = _hoveredNode is not null &&
                    (string.Equals(_hoveredNode.Id, a.Id, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(_hoveredNode.Id, b.Id, StringComparison.OrdinalIgnoreCase));

                context.DrawLine(highlighted ? activeEdgePen : defaultEdgePen, new Point(a.X, a.Y), new Point(b.X, b.Y));
            }
        }

        var ringPen = new Pen(new SolidColorBrush(Color.Parse(dark ? "#000000" : "#FFFFFF")), 2.5);
        var focusPen = new Pen(new SolidColorBrush(Color.Parse("#0071E3")), 3.5);
        var textBrush = new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#1D1D1F"));
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        foreach (var node in nodes)
        {
            bool isHovered = ReferenceEquals(node, _hoveredNode);
            double r = isHovered ? node.Radius + 3.0 : node.Radius;
            var fillBrush = new SolidColorBrush(Color.Parse(node.ColorHex));

            context.DrawEllipse(fillBrush, isHovered ? focusPen : ringPen, new Point(node.X, node.Y), r, r);

            var formatted = new FormattedText(
                $"{node.Title} ({node.PageRank:0.00})",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                textBrush);

            context.DrawText(formatted, new Point(node.X - formatted.Width / 2.0, node.Y + r + 5.0));
        }
    }
}
