using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rhyolite.Core.Canvas;

namespace Rhyolite.Controls;

/// <summary>
/// Interactive JSON Canvas 1.0 (.canvas) spatial board renderer with draggable cards,
/// group frames, and directional Bezier edges.
/// </summary>
public sealed class SpatialCanvasControl : Control
{
    public static readonly StyledProperty<CanvasDocument?> DocumentProperty =
        AvaloniaProperty.Register<SpatialCanvasControl, CanvasDocument?>(nameof(Document));

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<SpatialCanvasControl, bool>(nameof(IsDarkTheme));

    private CanvasNode? _draggingNode;
    private Point _dragOffset;

    public event Action<CanvasNode>? CardClicked;

    static SpatialCanvasControl()
    {
        AffectsRender<SpatialCanvasControl>(DocumentProperty, IsDarkThemeProperty);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var doc = Document;
        if (doc is null) return;

        var pos = e.GetPosition(this);
        // Hit test non-group cards first
        var hit = doc.Nodes
            .Where(n => !string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault(n => new Rect(n.X, n.Y, n.Width, n.Height).Contains(pos));

        if (hit is not null)
        {
            _draggingNode = hit;
            _dragOffset = new Point(pos.X - hit.X, pos.Y - hit.Y);
            CardClicked?.Invoke(hit);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_draggingNode is null) return;

        var pos = e.GetPosition(this);
        _draggingNode.X = Math.Clamp(pos.X - _dragOffset.X, 16, Math.Max(40, Bounds.Width - _draggingNode.Width - 16));
        _draggingNode.Y = Math.Clamp(pos.Y - _dragOffset.Y, 16, Math.Max(40, Bounds.Height - _draggingNode.Height - 16));
        InvalidateVisual();
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
        context.FillRectangle(new SolidColorBrush(Color.Parse(dark ? "#000000" : "#FFFFFF")), new Rect(0, 0, bounds.Width, bounds.Height));

        var dotBrush = new SolidColorBrush(Color.Parse(dark ? "#2C2C2E" : "#D2D2D7"));
        for (double gx = 20; gx < bounds.Width; gx += 20)
        {
            for (double gy = 20; gy < bounds.Height; gy += 20)
            {
                context.DrawEllipse(dotBrush, null, new Point(gx, gy), 1.1, 1.1);
            }
        }

        var doc = Document;
        if (doc is null) return;

        var nodeMap = doc.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var cardBgBrush = new SolidColorBrush(Color.Parse(dark ? "#1C1C1E" : "#F5F5F7"));
        var groupBgBrush = new SolidColorBrush(Color.Parse(dark ? "#12FFFFFF" : "#0A000000"));
        var borderPen = new Pen(new SolidColorBrush(Color.Parse(dark ? "#38383A" : "#D2D2D7")), 1.2);
        var titleBrush = new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#1D1D1F"));
        var bodyBrush = new SolidColorBrush(Color.Parse(dark ? "#98989D" : "#424245"));
        var titleFace = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var bodyFace = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

        // 1. Render Groups
        foreach (var group in doc.Nodes.Where(n => string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase)))
        {
            var rect = new Rect(group.X, group.Y, group.Width, group.Height);
            context.DrawRectangle(groupBgBrush, borderPen, new RoundedRect(rect, 18));

            if (!string.IsNullOrWhiteSpace(group.Label))
            {
                var labelText = new FormattedText(
                    group.Label.ToUpperInvariant(),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    titleFace,
                    12,
                    bodyBrush);
                context.DrawText(labelText, new Point(group.X + 16, group.Y + 12));
            }
        }

        // 2. Render Edges
        foreach (var edge in doc.Edges)
        {
            if (!nodeMap.TryGetValue(edge.FromNode, out var from) || !nodeMap.TryGetValue(edge.ToNode, out var to))
                continue;

            var start = new Point(from.X + from.Width, from.Y + from.Height / 2.0);
            var end = new Point(to.X, to.Y + to.Height / 2.0);
            var edgeColor = Color.Parse(edge.Color ?? "#0071E3");
            var edgePen = new Pen(new SolidColorBrush(edgeColor), 2.2);

            context.DrawLine(edgePen, start, end);
            context.DrawEllipse(new SolidColorBrush(edgeColor), null, end, 4.0, 4.0);
        }

        // 3. Render Cards
        foreach (var card in doc.Nodes.Where(n => !string.Equals(n.Type, "group", StringComparison.OrdinalIgnoreCase)))
        {
            var rect = new Rect(card.X, card.Y, card.Width, card.Height);
            context.DrawRectangle(cardBgBrush, borderPen, new RoundedRect(rect, 12));

            // Top Apple accent strip
            var accentBrush = new SolidColorBrush(Color.Parse(card.Color ?? "#0071E3"));
            context.FillRectangle(accentBrush, new Rect(card.X + 12, card.Y + 8, card.Width - 24, 3.5));

            var title = new FormattedText(
                card.Label ?? card.File ?? "Canvas Card",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                titleFace,
                13,
                titleBrush)
            {
                MaxTextWidth = Math.Max(40, card.Width - 24)
            };
            context.DrawText(title, new Point(card.X + 12, card.Y + 20));

            if (!string.IsNullOrWhiteSpace(card.Text))
            {
                var body = new FormattedText(
                    card.Text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    bodyFace,
                    12,
                    bodyBrush)
                {
                    MaxTextWidth = Math.Max(40, card.Width - 24),
                    MaxTextHeight = Math.Max(20, card.Height - 52)
                };
                context.DrawText(body, new Point(card.X + 12, card.Y + 46));
            }
        }
    }
}
