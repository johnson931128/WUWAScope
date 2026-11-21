using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WUWAScope.Simulation.Models;

namespace WUWAScope.App.Controls;

public sealed class IsometricTopologyCanvas : Control
{
    private const double IsoTileW = 68.0;
    private const double IsoTileH = 38.0;

    private static readonly Color PaperBg = Color.Parse("#FAF6EE");
    private static readonly Color GridLineColor = Color.Parse("#EDE5D5");
    private static readonly Color InkColor = Color.Parse("#1E2430");
    private static readonly Color MutedInkColor = Color.Parse("#5E6674");
    private static readonly Color ShadowColor = Color.Parse("#221E2430");
    private static readonly Color HighlightGreen = Color.Parse("#2E9E63");
    private static readonly Color WarningAmber = Color.Parse("#D9822B");
    private static readonly Color FailedCoral = Color.Parse("#D9534F");

    private static readonly IBrush PaperBrush = new SolidColorBrush(PaperBg);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(GridLineColor), 1.0);
    private static readonly IBrush InkBrush = new SolidColorBrush(InkColor);
    private static readonly IBrush MutedInkBrush = new SolidColorBrush(MutedInkColor);
    private static readonly IBrush WhiteBrush = Brushes.White;
    private static readonly IBrush ShadowBrush = new SolidColorBrush(ShadowColor);
    private static readonly IPen InkOutlinePen = new Pen(InkBrush, 2.0, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
    private static readonly IPen SoftCreasePen = new Pen(new SolidColorBrush(Color.Parse("#361E2430")), 1.2, lineJoin: PenLineJoin.Round);
    private static readonly IPen PillBorderPen = new Pen(InkBrush, 1.5, lineJoin: PenLineJoin.Round);
    private static readonly IPen ActiveLinkPen = new Pen(InkBrush, 2.1, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
    private static readonly IPen HighlightLinkPen = new Pen(new SolidColorBrush(HighlightGreen), 2.8, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
    private static readonly IPen DashedLinkPen = new Pen(
        new SolidColorBrush(Color.Parse("#8B929E")),
        1.6,
        dashStyle: new DashStyle([5, 5], 0),
        lineJoin: PenLineJoin.Round,
        lineCap: PenLineCap.Round);
    private static readonly IPen SelectionHaloPen = new Pen(
        new SolidColorBrush(HighlightGreen),
        2.4,
        dashStyle: new DashStyle([6, 4], 0),
        lineJoin: PenLineJoin.Round);

    private readonly List<HitTarget> _hitTargets = [];
    private double _zoom = 1.0;
    private Point _panOffset = new(0, 0);
    private bool _hasAutoFit;
    private bool _isPanning;
    private Point _panStartScreen;
    private Point _panStartOffset;
    private string? _hoveredId;
    private DateTime _lastClickTime;
    private string? _lastClickedId;

    public static readonly StyledProperty<SimulationState?> SimulationStateProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, SimulationState?>(nameof(SimulationState));

    public static readonly StyledProperty<NodeState?> ActiveNodeProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, NodeState?>(nameof(ActiveNode));

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, string?>(nameof(SelectedNodeId));

    public static readonly StyledProperty<string?> SelectedModuleIdProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, string?>(nameof(SelectedModuleId));

    public static readonly StyledProperty<EventRecord?> HighlightedEventProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, EventRecord?>(nameof(HighlightedEvent));

    public static readonly StyledProperty<string> ZoomLabelProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, string>(nameof(ZoomLabel), "100%");

    static IsometricTopologyCanvas()
    {
        AffectsRender<IsometricTopologyCanvas>(
            SimulationStateProperty,
            ActiveNodeProperty,
            SelectedNodeIdProperty,
            SelectedModuleIdProperty,
            HighlightedEventProperty);
    }

    public IsometricTopologyCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public SimulationState? SimulationState
    {
        get => GetValue(SimulationStateProperty);
        set => SetValue(SimulationStateProperty, value);
    }

    public NodeState? ActiveNode
    {
        get => GetValue(ActiveNodeProperty);
        set => SetValue(ActiveNodeProperty, value);
    }

    public string? SelectedNodeId
    {
        get => GetValue(SelectedNodeIdProperty);
        set => SetValue(SelectedNodeIdProperty, value);
    }

    public string? SelectedModuleId
    {
        get => GetValue(SelectedModuleIdProperty);
        set => SetValue(SelectedModuleIdProperty, value);
    }

    public EventRecord? HighlightedEvent
    {
        get => GetValue(HighlightedEventProperty);
        set => SetValue(HighlightedEventProperty, value);
    }

    public string ZoomLabel
    {
        get => GetValue(ZoomLabelProperty);
        private set => SetValue(ZoomLabelProperty, value);
    }

    public event Action<string?>? NodeSelected;
    public event Action<string>? NodeDoubleClicked;
    public event Action<string?>? ModuleSelected;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActiveNodeProperty)
        {
            _hoveredId = null;
            FitToView();
        }
        else if (change.Property == BoundsProperty && !_hasAutoFit && Bounds.Width > 100 && Bounds.Height > 100)
        {
            FitToView();
        }
    }

    public void ZoomIn()
    {
        SetZoom(Math.Clamp(_zoom * 1.15, 0.45, 2.2), new Point(Bounds.Width / 2, Bounds.Height / 2));
    }

    public void ZoomOut()
    {
        SetZoom(Math.Clamp(_zoom / 1.15, 0.45, 2.2), new Point(Bounds.Width / 2, Bounds.Height / 2));
    }

    public void FitToView()
    {
        if (Bounds.Width <= 40 || Bounds.Height <= 40)
        {
            return;
        }

        var worldBounds = ComputeCurrentWorldBounds();
        if (worldBounds.Width <= 1 || worldBounds.Height <= 1)
        {
            _zoom = 1.0;
            _panOffset = new Point(Bounds.Width / 2, Bounds.Height / 2);
            ZoomLabel = "100%";
            InvalidateVisual();
            return;
        }

        const double paddingX = 96;
        const double paddingY = 92;
        var availW = Math.Max(120, Bounds.Width - paddingX * 2);
        var availH = Math.Max(120, Bounds.Height - paddingY * 2);

        var scale = Math.Min(availW / worldBounds.Width, availH / worldBounds.Height);
        _zoom = Math.Clamp(scale, 0.55, 1.35);

        var worldCenter = worldBounds.Center;
        _panOffset = new Point(
            Bounds.Width / 2 - worldCenter.X * _zoom,
            Bounds.Height / 2 - worldCenter.Y * _zoom);

        _hasAutoFit = true;
        ZoomLabel = $"{Math.Round(_zoom * 100)}%";
        InvalidateVisual();
    }

    private void SetZoom(double newZoom, Point anchorScreen)
    {
        var worldBefore = ScreenToWorld(anchorScreen);
        _zoom = newZoom;
        _panOffset = new Point(
            anchorScreen.X - worldBefore.X * _zoom,
            anchorScreen.Y - worldBefore.Y * _zoom);
        ZoomLabel = $"{Math.Round(_zoom * 100)}%";
        InvalidateVisual();
    }

    private Point WorldToScreen(Point world) =>
        new(world.X * _zoom + _panOffset.X, world.Y * _zoom + _panOffset.Y);

    private Point ScreenToWorld(Point screen) =>
        new((screen.X - _panOffset.X) / _zoom, (screen.Y - _panOffset.Y) / _zoom);

    private static Point GridToWorld(double gx, double gy) =>
        new((gx - gy) * IsoTileW, (gx + gy) * IsoTileH);

    private Rect ComputeCurrentWorldBounds()
    {
        var points = new List<Point>();

        if (ActiveNode is { } activeNode)
        {
            foreach (var m in activeNode.Modules)
            {
                var p = GridToWorld(m.GridX, m.GridY);
                points.Add(new Point(p.X - 90, p.Y - 75));
                points.Add(new Point(p.X + 90, p.Y + 95));
            }
        }
        else if (SimulationState is { } state)
        {
            foreach (var n in state.Nodes)
            {
                var p = GridToWorld(n.GridX, n.GridY);
                points.Add(new Point(p.X - 110, p.Y - 90));
                points.Add(new Point(p.X + 110, p.Y + 110));
            }
        }

        if (points.Count == 0)
        {
            return new Rect(-250, -180, 500, 360);
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _hitTargets.Clear();

        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(PaperBrush, rect);

        DrawIsometricGrid(context, rect);

        if (SimulationState is null)
        {
            return;
        }

        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y)))
        {
            if (ActiveNode is { } activeNode)
            {
                RenderNodeInternalView(context, activeNode);
            }
            else
            {
                RenderClusterView(context, SimulationState);
            }
        }
    }

    private void DrawIsometricGrid(DrawingContext ctx, Rect viewport)
    {
        var topLeft = ScreenToWorld(viewport.TopLeft);
        var bottomRight = ScreenToWorld(viewport.BottomRight);

        const double cellW = 54.0;
        const double cellH = 30.0;

        // Draw crisp diagonal diamond grid lines in world space
        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y)))
        {
            var minX = topLeft.X - cellW * 4;
            var maxX = bottomRight.X + cellW * 4;
            var minY = topLeft.Y - cellH * 4;
            var maxY = bottomRight.Y + cellH * 4;

            var slope = cellH / cellW;
            var step = cellH * 2;

            // Family 1: y = slope * x + b
            var bMin1 = Math.Floor((minY - slope * maxX) / step) * step;
            var bMax1 = Math.Ceiling((maxY - slope * minX) / step) * step;
            for (var b = bMin1; b <= bMax1; b += step)
            {
                var p1 = new Point(minX, slope * minX + b);
                var p2 = new Point(maxX, slope * maxX + b);
                ctx.DrawLine(GridPen, p1, p2);
            }

            // Family 2: y = -slope * x + b
            var bMin2 = Math.Floor((minY + slope * minX) / step) * step;
            var bMax2 = Math.Ceiling((maxY + slope * maxX) / step) * step;
            for (var b = bMin2; b <= bMax2; b += step)
            {
                var p1 = new Point(minX, -slope * minX + b);
                var p2 = new Point(maxX, -slope * maxX + b);
                ctx.DrawLine(GridPen, p1, p2);
            }
        }
    }

    private void RenderClusterView(DrawingContext ctx, SimulationState state)
    {
        var nodeCenters = state.Nodes.ToDictionary(n => n.Id, n => GridToWorld(n.GridX, n.GridY));

        // 1. Draw network links first (under nodes)
        foreach (var link in state.Links)
        {
            if (!nodeCenters.TryGetValue(link.SourceNodeId, out var from) ||
                !nodeCenters.TryGetValue(link.TargetNodeId, out var to))
            {
                continue;
            }

            var isEventLink = HighlightedEvent is not null &&
                              ((HighlightedEvent.SourceNodeId == link.SourceNodeId && HighlightedEvent.TargetNodeId == link.TargetNodeId) ||
                               (HighlightedEvent.SourceNodeId == link.TargetNodeId && HighlightedEvent.TargetNodeId == link.SourceNodeId));

            var linkMessages = state.InFlightMessages
                .Where(m =>
                    (m.SourceNodeId == link.SourceNodeId && m.TargetNodeId == link.TargetNodeId) ||
                    (m.SourceNodeId == link.TargetNodeId && m.TargetNodeId == link.SourceNodeId))
                .ToList();

            DrawIsometricLink(
                ctx,
                from,
                to,
                link.Label,
                link.IsHeartbeatChannel,
                isEventLink,
                linkMessages,
                state.SimulatedTimeMs);
        }

        // 2. Draw nodes in back-to-front painter's order (ascending world Y)
        foreach (var node in state.Nodes.OrderBy(n => GridToWorld(n.GridX, n.GridY).Y))
        {
            var center = nodeCenters[node.Id];
            var isSelected = node.Id == SelectedNodeId;
            var isHovered = node.Id == _hoveredId;
            var isEventNode = HighlightedEvent is not null &&
                              (HighlightedEvent.SourceNodeId == node.Id || HighlightedEvent.TargetNodeId == node.Id);

            if (node.Role == NodeRole.Client)
            {
                DrawClientLaptopNode(ctx, node, center, isSelected || isEventNode, isHovered);
            }
            else
            {
                DrawServerCubeNode(ctx, node, center, isSelected || isEventNode, isHovered);
            }
        }
    }

    private void RenderNodeInternalView(DrawingContext ctx, NodeState node)
    {
        var modCenters = node.Modules.ToDictionary(m => m.Id, m => GridToWorld(m.GridX, m.GridY));

        // 1. Draw large isometric chassis floorplate underneath internal modules
        if (modCenters.Count > 0)
        {
            var minX = modCenters.Values.Min(p => p.X) - 105;
            var maxX = modCenters.Values.Max(p => p.X) + 105;
            var minY = modCenters.Values.Min(p => p.Y) - 80;
            var maxY = modCenters.Values.Max(p => p.Y) + 95;

            var plateRect = new Rect(minX, minY, maxX - minX, maxY - minY);
            DrawChassisFloorplate(ctx, plateRect, node);
        }

        // 2. Draw internal flow links between modules
        foreach (var link in node.InternalLinks)
        {
            if (!modCenters.TryGetValue(link.SourceModuleId, out var from) ||
                !modCenters.TryGetValue(link.TargetModuleId, out var to))
            {
                continue;
            }

            var isHighlighted = link.SourceModuleId == SelectedModuleId || link.TargetModuleId == SelectedModuleId;
            DrawModuleFlowArrow(ctx, from, to, link.Label, link.IsDashed, isHighlighted);
        }

        // 3. Draw internal module 2.5D blocks back-to-front
        foreach (var module in node.Modules.OrderBy(m => GridToWorld(m.GridX, m.GridY).Y))
        {
            var center = modCenters[module.Id];
            var isSelected = module.Id == SelectedModuleId;
            var isHovered = module.Id == _hoveredId;
            var isEventModule = HighlightedEvent?.ModuleId == module.Id;

            DrawModuleBlock(ctx, module, center, isSelected || isEventModule, isHovered);
        }
    }

    private void DrawChassisFloorplate(DrawingContext ctx, Rect rect, NodeState node)
    {
        var shadowRect = new Rect(rect.X + 4, rect.Y + 10, rect.Width, rect.Height);
        ctx.DrawRectangle(ShadowBrush, null, shadowRect, 22, 22);

        var sideRect = new Rect(rect.X, rect.Y + 8, rect.Width, rect.Height);
        var sideColor = node.Role == NodeRole.Coordinator
            ? Color.Parse("#EAD4A2")
            : node.Role == NodeRole.Worker
                ? Color.Parse("#C9DCF5")
                : Color.Parse("#E3DAC9");
        ctx.DrawRectangle(new SolidColorBrush(sideColor), InkOutlinePen, sideRect, 22, 22);

        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#FFFDF8")), InkOutlinePen, rect, 22, 22);

        // Header pill sitting cleanly on top-left edge of floorplate
        DrawPillBadge(
            ctx,
            new Point(rect.X + 130, rect.Y),
            $"Inside {node.Name} · {node.Modules.Count} Modules",
            GetHealthAccentColor(node.Health),
            fontSize: 12.5,
            bold: true);
    }

    private void DrawClientLaptopNode(DrawingContext ctx, NodeState node, Point center, bool isSelected, bool isHovered)
    {
        var lift = isHovered ? -4.0 : 0.0;
        var c = new Point(center.X, center.Y + lift);
        const double halfW = 86;
        const double halfH = 48;
        const double depth = 18;

        // Ground shadow
        DrawRoundedDiamond(ctx, new Point(center.X, center.Y + depth + 8), halfW + 4, halfH + 3, 14, ShadowBrush, null);

        if (isSelected)
        {
            DrawRoundedDiamond(ctx, new Point(c.X, c.Y + depth / 2), halfW + 13, halfH + 10, 18, null, SelectionHaloPen);
        }

        // Rounded stone isometric base platform
        DrawIsometricSlab(
            ctx,
            c,
            halfW,
            halfH,
            depth,
            topColor: Color.Parse("#F5EFE4"),
            leftColor: Color.Parse("#E5DCCB"),
            rightColor: Color.Parse("#D7CCB8"),
            cornerRadius: 14);

        // Stylized isometric laptop keyboard deck on platform
        var kbCenter = new Point(c.X + 6, c.Y + 6);
        DrawRoundedDiamond(
            ctx,
            kbCenter,
            40,
            21,
            5,
            new SolidColorBrush(Color.Parse("#FFFFFF")),
            new Pen(InkBrush, 1.5, lineJoin: PenLineJoin.Round));

        // Tiny keyboard dots
        var keyBrush = new SolidColorBrush(Color.Parse("#C8BFAF"));
        for (var row = -1; row <= 1; row++)
        {
            for (var col = -2; col <= 1; col++)
            {
                var kx = kbCenter.X + col * 8 - row * 5;
                var ky = kbCenter.Y + col * 4.2 + row * 3.5 - 2;
                ctx.DrawEllipse(keyBrush, null, new Point(kx, ky), 2.1, 1.3);
            }
        }

        // Stylized tilted laptop screen
        var screenGeo = new StreamGeometry();
        using (var g = screenGeo.Open())
        {
            g.BeginFigure(new Point(c.X - 38, c.Y - 4), true);
            g.LineTo(new Point(c.X + 8, c.Y - 29));
            g.LineTo(new Point(c.X + 8, c.Y - 70));
            g.LineTo(new Point(c.X - 38, c.Y - 45));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#2B313F")), InkOutlinePen, screenGeo);

        var innerScreen = new StreamGeometry();
        using (var g = innerScreen.Open())
        {
            g.BeginFigure(new Point(c.X - 33, c.Y - 10), true);
            g.LineTo(new Point(c.X + 3, c.Y - 29));
            g.LineTo(new Point(c.X + 3, c.Y - 62));
            g.LineTo(new Point(c.X - 33, c.Y - 43));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#FAF7F2")), null, innerScreen);

        // Mini app content bar inside laptop screen
        var barGeo = new StreamGeometry();
        using (var g = barGeo.Open())
        {
            g.BeginFigure(new Point(c.X - 28, c.Y - 30), true);
            g.LineTo(new Point(c.X - 2, c.Y - 44));
            g.LineTo(new Point(c.X - 2, c.Y - 52));
            g.LineTo(new Point(c.X - 28, c.Y - 38));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#C8BDF0")), null, barGeo);

        // Top floating pill ("Client · Job J1")
        DrawPillBadge(ctx, new Point(c.X - 10, c.Y - 84), "Client · Job Submitter", HighlightGreen, fontSize: 12, bold: true);

        // Bottom status pill
        DrawPillBadge(
            ctx,
            new Point(c.X, c.Y + halfH + depth + 16),
            FormatHealthPill(node.Health),
            GetHealthAccentColor(node.Health),
            fontSize: 11,
            bold: false);

        _hitTargets.Add(new HitTarget(node.Id, IsModule: false, new Rect(c.X - halfW, c.Y - 92, halfW * 2, halfH * 2 + depth + 100)));
    }

    private void DrawServerCubeNode(DrawingContext ctx, NodeState node, Point center, bool isSelected, bool isHovered)
    {
        var lift = isHovered ? -4.0 : 0.0;
        var c = new Point(center.X, center.Y + lift);
        var isCoord = node.Role == NodeRole.Coordinator;
        var halfW = isCoord ? 76.0 : 66.0;
        var halfH = isCoord ? 43.0 : 37.0;
        var depth = isCoord ? 34.0 : 30.0;

        var (topColor, leftColor, rightColor) = GetNodePalette(node);

        // Soft ground shadow
        DrawRoundedDiamond(ctx, new Point(center.X, center.Y + depth + 10), halfW + 3, halfH + 2, 14, ShadowBrush, null);

        if (isSelected)
        {
            DrawRoundedDiamond(ctx, new Point(c.X, c.Y + depth * 0.45), halfW + 13, halfH + 10, 18, null, SelectionHaloPen);
        }

        DrawIsometricSlab(ctx, c, halfW, halfH, depth, topColor, leftColor, rightColor, cornerRadius: 14);

        // Role minimalist icon centered on top face
        if (isCoord)
        {
            DrawCoordinatorIcon(ctx, new Point(c.X, c.Y - 12));
        }
        else
        {
            DrawWorkerServerIcon(ctx, new Point(c.X, c.Y - 10));
        }

        // Crisp label on top face
        DrawCenteredText(ctx, node.Name, new Point(c.X, c.Y + 12), 13.5, InkBrush, FontWeight.Bold);

        // Role tag / subtitle below node as crisp pill
        var pillText = node.Health == NodeHealthStatus.Processing && node.ActiveTaskCount > 0
            ? $"{FormatHealthPill(node.Health)} · {node.ActiveTaskCount} task"
            : FormatHealthPill(node.Health);

        DrawPillBadge(
            ctx,
            new Point(c.X, c.Y + halfH + depth + 14),
            pillText,
            GetHealthAccentColor(node.Health),
            fontSize: 11,
            bold: false);

        _hitTargets.Add(new HitTarget(node.Id, IsModule: false, new Rect(c.X - halfW, c.Y - halfH, halfW * 2, halfH * 2 + depth + 30)));
    }

    private void DrawModuleBlock(DrawingContext ctx, ModuleState module, Point center, bool isSelected, bool isHovered)
    {
        var lift = isHovered ? -3.0 : 0.0;
        var c = new Point(center.X, center.Y + lift);
        const double halfW = 62;
        const double halfH = 34;
        const double depth = 22;

        var (topColor, leftColor, rightColor) = GetModulePalette(module.Status);

        DrawRoundedDiamond(ctx, new Point(center.X, center.Y + depth + 7), halfW + 2, halfH + 2, 11, ShadowBrush, null);

        if (isSelected)
        {
            DrawRoundedDiamond(ctx, new Point(c.X, c.Y + depth * 0.4), halfW + 10, halfH + 8, 14, null, SelectionHaloPen);
        }

        DrawIsometricSlab(ctx, c, halfW, halfH, depth, topColor, leftColor, rightColor, cornerRadius: 11);

        DrawCenteredText(ctx, module.Name, new Point(c.X, c.Y - 3), 12, InkBrush, FontWeight.Bold);
        DrawCenteredText(ctx, FormatModuleShortStatus(module.Status), new Point(c.X, c.Y + 13), 10.5, MutedInkBrush, FontWeight.SemiBold);

        if (!string.IsNullOrWhiteSpace(module.ActiveHint))
        {
            DrawPillBadge(
                ctx,
                new Point(c.X, c.Y + halfH + depth + 10),
                module.ActiveHint,
                GetModuleAccentColor(module.Status),
                fontSize: 10.5,
                bold: false);
        }

        _hitTargets.Add(new HitTarget(module.Id, IsModule: true, new Rect(c.X - halfW, c.Y - halfH, halfW * 2, halfH * 2 + depth + 24)));
    }

    private void DrawIsometricLink(
        DrawingContext ctx,
        Point from,
        Point to,
        string label,
        bool isHeartbeat,
        bool isHighlighted,
        IReadOnlyList<MessageState> messages,
        long simulatedTimeMs)
    {
        // Trim start/end slightly away from node centers so arrowheads touch node rims cleanly
        var dir = to - from;
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1)
        {
            return;
        }

        var ux = dir.X / len;
        var uy = dir.Y / len;
        var start = new Point(from.X + ux * 62, from.Y + uy * 36 + 10);
        var end = new Point(to.X - ux * 62, to.Y - uy * 36 + 10);

        // Build a gentle bent path for visual elegance
        var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var perpOffset = isHeartbeat ? -14.0 : 14.0;
        var ctrl = new Point(mid.X - uy * perpOffset, mid.Y + ux * perpOffset);

        var pathGeo = new StreamGeometry();
        using (var g = pathGeo.Open())
        {
            g.BeginFigure(start, false);
            g.QuadraticBezierTo(ctrl, end);
            g.EndFigure(false);
        }

        var pen = isHighlighted
            ? HighlightLinkPen
            : isHeartbeat
                ? DashedLinkPen
                : ActiveLinkPen;

        ctx.DrawGeometry(null, pen, pathGeo);
        DrawArrowHead(ctx, ctrl, end, isHighlighted ? new SolidColorBrush(HighlightGreen) : (isHeartbeat ? MutedInkBrush : InkBrush));

        // Compute curve label position slightly closer to target so labels never crowd Coordinator's badge
        var labelT = isHeartbeat ? 0.62 : 0.52;
        var curveLabelPos = EvalQuadratic(start, ctrl, end, labelT);

        // Draw state-driven in-flight packet dots along the curve
        foreach (var msg in messages)
        {
            var span = Math.Max(1, msg.DeliverAtMs - msg.SentAtMs);
            var progress = Math.Clamp((simulatedTimeMs - msg.SentAtMs) / (double)span, 0.25, 0.78);
            var reversed = msg.Kind == MessageKind.Heartbeat;
            var t = reversed ? 1.0 - progress : progress;

            var ptMain = EvalQuadratic(start, ctrl, end, t);
            var ptTrail = EvalQuadratic(start, ctrl, end, Math.Clamp(t - (reversed ? -0.09 : 0.09), 0.12, 0.88));

            var dotColor = msg.Kind == MessageKind.Heartbeat ? Color.Parse("#4B8BF5") : HighlightGreen;
            var dotPen = new Pen(InkBrush, 1.8);

            // Trailing hollow dot + leading solid dot (clean editorial packet grammar)
            ctx.DrawEllipse(WhiteBrush, new Pen(new SolidColorBrush(dotColor), 2.0), ptTrail, 5.0, 5.0);
            ctx.DrawEllipse(new SolidColorBrush(dotColor), dotPen, ptMain, 6.0, 6.0);
        }

        // Draw crisp white pill label cleanly offset from the link curve
        var isVerticalLink = Math.Abs(end.Y - start.Y) > Math.Abs(end.X - start.X) * 1.4;
        var pillOffset = isVerticalLink
            ? new Point(curveLabelPos.X + 56, curveLabelPos.Y + 6)
            : new Point(curveLabelPos.X, curveLabelPos.Y + (isHeartbeat ? 22 : -22));

        DrawPillBadge(
            ctx,
            pillOffset,
            label,
            isHeartbeat ? Color.Parse("#8B929E") : HighlightGreen,
            fontSize: 10.5,
            bold: false,
            showDot: false);
    }

    private static void DrawModuleFlowArrow(
        DrawingContext ctx,
        Point from,
        Point to,
        string label,
        bool isDashed,
        bool isHighlighted)
    {
        var dir = to - from;
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1)
        {
            return;
        }

        var ux = dir.X / len;
        var uy = dir.Y / len;
        var start = new Point(from.X + ux * 46, from.Y + uy * 26 + 6);
        var end = new Point(to.X - ux * 46, to.Y - uy * 26 + 6);

        var pen = isHighlighted
            ? HighlightLinkPen
            : isDashed
                ? DashedLinkPen
                : ActiveLinkPen;

        ctx.DrawLine(pen, start, end);
        DrawArrowHead(ctx, start, end, isHighlighted ? new SolidColorBrush(HighlightGreen) : (isDashed ? MutedInkBrush : InkBrush));

        var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2 - 12);
        DrawPillBadge(ctx, mid, label, HighlightGreen, fontSize: 10, bold: false, showDot: false);
    }

    private static void DrawArrowHead(DrawingContext ctx, Point from, Point to, IBrush brush)
    {
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double size = 8.5;

        var p1 = new Point(
            to.X - size * Math.Cos(angle - 0.45),
            to.Y - size * Math.Sin(angle - 0.45));
        var p2 = new Point(
            to.X - size * Math.Cos(angle + 0.45),
            to.Y - size * Math.Sin(angle + 0.45));

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(p1);
            g.LineTo(p2);
            g.EndFigure(true);
        }

        ctx.DrawGeometry(brush, null, geo);
    }

    private static void DrawIsometricSlab(
        DrawingContext ctx,
        Point center,
        double halfW,
        double halfH,
        double depth,
        Color topColor,
        Color leftColor,
        Color rightColor,
        double cornerRadius)
    {
        // Left shaded face
        var leftFace = new StreamGeometry();
        using (var g = leftFace.Open())
        {
            g.BeginFigure(new Point(center.X - halfW, center.Y), true);
            g.LineTo(new Point(center.X, center.Y + halfH));
            g.LineTo(new Point(center.X, center.Y + halfH + depth));
            g.LineTo(new Point(center.X - halfW, center.Y + depth));
            g.EndFigure(true);
        }

        // Right shaded face
        var rightFace = new StreamGeometry();
        using (var g = rightFace.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y + halfH), true);
            g.LineTo(new Point(center.X + halfW, center.Y));
            g.LineTo(new Point(center.X + halfW, center.Y + depth));
            g.LineTo(new Point(center.X, center.Y + halfH + depth));
            g.EndFigure(true);
        }

        // Outer silhouette path with rounded bottom corners
        var silhouette = BuildSlabSilhouette(center, halfW, halfH, depth, cornerRadius);

        // Clip faces inside rounded silhouette for clean rounded isometric corners
        using (ctx.PushGeometryClip(silhouette))
        {
            ctx.DrawGeometry(new SolidColorBrush(leftColor), null, leftFace);
            ctx.DrawGeometry(new SolidColorBrush(rightColor), null, rightFace);
            DrawRoundedDiamond(ctx, center, halfW, halfH, cornerRadius, new SolidColorBrush(topColor), SoftCreasePen);
            ctx.DrawLine(SoftCreasePen, new Point(center.X, center.Y + halfH - 2), new Point(center.X, center.Y + halfH + depth));
        }

        // Crisp dark ink outer contour
        ctx.DrawGeometry(null, InkOutlinePen, silhouette);
    }

    private static StreamGeometry BuildSlabSilhouette(Point c, double hw, double hh, double d, double r)
    {
        var pts = new[]
        {
            new Point(c.X, c.Y - hh),             // Top
            new Point(c.X + hw, c.Y),             // Top-right
            new Point(c.X + hw, c.Y + d),         // Bottom-right
            new Point(c.X, c.Y + hh + d),         // Bottom
            new Point(c.X - hw, c.Y + d),         // Bottom-left
            new Point(c.X - hw, c.Y)              // Top-left
        };

        return BuildRoundedPolygon(pts, r);
    }

    private static void DrawRoundedDiamond(
        DrawingContext ctx,
        Point c,
        double hw,
        double hh,
        double r,
        IBrush? fill,
        IPen? pen)
    {
        var pts = new[]
        {
            new Point(c.X, c.Y - hh),
            new Point(c.X + hw, c.Y),
            new Point(c.X, c.Y + hh),
            new Point(c.X - hw, c.Y)
        };

        var geo = BuildRoundedPolygon(pts, r);
        ctx.DrawGeometry(fill, pen, geo);
    }

    private static StreamGeometry BuildRoundedPolygon(Point[] pts, double radius)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        var count = pts.Length;

        for (var i = 0; i < count; i++)
        {
            var prev = pts[(i - 1 + count) % count];
            var curr = pts[i];
            var next = pts[(i + 1) % count];

            var v1 = prev - curr;
            var v2 = next - curr;
            var l1 = Math.Sqrt(v1.X * v1.X + v1.Y * v1.Y);
            var l2 = Math.Sqrt(v2.X * v2.X + v2.Y * v2.Y);
            var r = Math.Min(radius, Math.Min(l1, l2) * 0.42);

            var pStart = new Point(curr.X + v1.X / l1 * r, curr.Y + v1.Y / l1 * r);
            var pEnd = new Point(curr.X + v2.X / l2 * r, curr.Y + v2.Y / l2 * r);

            if (i == 0)
            {
                g.BeginFigure(pStart, true);
            }
            else
            {
                g.LineTo(pStart);
            }

            g.QuadraticBezierTo(curr, pEnd);
        }

        g.EndFigure(true);
        return geo;
    }

    private static void DrawCoordinatorIcon(DrawingContext ctx, Point center)
    {
        var iconPen = new Pen(InkBrush, 1.7);
        const double r = 10.5;
        ctx.DrawEllipse(null, iconPen, center, r, r);
        ctx.DrawEllipse(null, iconPen, center, r * 0.48, r);
        ctx.DrawLine(iconPen, new Point(center.X - r, center.Y), new Point(center.X + r, center.Y));
    }

    private static void DrawWorkerServerIcon(DrawingContext ctx, Point center)
    {
        var iconPen = new Pen(InkBrush, 1.6);
        var topBox = new Rect(center.X - 11, center.Y - 9, 22, 8);
        var botBox = new Rect(center.X - 11, center.Y + 2, 22, 8);
        ctx.DrawRectangle(null, iconPen, topBox, 2, 2);
        ctx.DrawRectangle(null, iconPen, botBox, 2, 2);

        ctx.DrawEllipse(InkBrush, null, new Point(center.X - 6.5, center.Y - 5), 1.4, 1.4);
        ctx.DrawEllipse(InkBrush, null, new Point(center.X - 6.5, center.Y + 6), 1.4, 1.4);
        ctx.DrawLine(iconPen, new Point(center.X - 2, center.Y - 5), new Point(center.X + 7, center.Y - 5));
        ctx.DrawLine(iconPen, new Point(center.X - 2, center.Y + 6), new Point(center.X + 7, center.Y + 6));
    }

    private static void DrawPillBadge(
        DrawingContext ctx,
        Point center,
        string text,
        Color dotColor,
        double fontSize,
        bool bold,
        bool showDot = true)
    {
        var typeface = new Typeface("Inter, Segoe UI, Arial", FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.SemiBold);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, InkBrush);

        var dotSpace = showDot ? 13.0 : 0.0;
        var padX = 11.0;
        var padY = 4.5;
        var w = ft.Width + padX * 2 + dotSpace;
        var h = ft.Height + padY * 2;
        var pillRect = new Rect(center.X - w / 2, center.Y - h / 2, w, h);

        // Soft drop shadow
        ctx.DrawRectangle(ShadowBrush, null, new Rect(pillRect.X, pillRect.Y + 2.5, w, h), h / 2, h / 2);
        // White pill with crisp dark border
        ctx.DrawRectangle(WhiteBrush, PillBorderPen, pillRect, h / 2, h / 2);

        if (showDot)
        {
            ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(pillRect.X + padX + 3.5, center.Y), 3.8, 3.8);
        }

        ctx.DrawText(ft, new Point(pillRect.X + padX + dotSpace, center.Y - ft.Height / 2));
    }

    private static void DrawCenteredText(DrawingContext ctx, string text, Point center, double fontSize, IBrush brush, FontWeight weight)
    {
        var typeface = new Typeface("Inter, Segoe UI, Arial", FontStyle.Normal, weight);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, brush);
        ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static Point EvalQuadratic(Point p0, Point p1, Point p2, double t)
    {
        var inv = 1.0 - t;
        return new Point(
            inv * inv * p0.X + 2 * inv * t * p1.X + t * t * p2.X,
            inv * inv * p0.Y + 2 * inv * t * p1.Y + t * t * p2.Y);
    }

    private static (Color Top, Color Left, Color Right) GetNodePalette(NodeState node)
    {
        if (node.Health == NodeHealthStatus.Failed)
        {
            return (Color.Parse("#F5C4C0"), Color.Parse("#E4A5A0"), Color.Parse("#D68E88"));
        }

        if (node.Role == NodeRole.Coordinator)
        {
            return (Color.Parse("#FAE0A6"), Color.Parse("#EEC87B"), Color.Parse("#E0B55E"));
        }

        // Workers
        return (Color.Parse("#C9DFFB"), Color.Parse("#AFCBF5"), Color.Parse("#94B6EB"));
    }

    private static (Color Top, Color Left, Color Right) GetModulePalette(ModuleStatus status) => status switch
    {
        ModuleStatus.Processing => (Color.Parse("#D6F2E0"), Color.Parse("#B5E2C6"), Color.Parse("#96D3AD")),
        ModuleStatus.Queued => (Color.Parse("#FCE2C4"), Color.Parse("#F2C899"), Color.Parse("#E6B37C")),
        ModuleStatus.Healthy => (Color.Parse("#CFE2FC"), Color.Parse("#B5CFF6"), Color.Parse("#9BBBED")),
        ModuleStatus.Suspected => (Color.Parse("#FCE8B3"), Color.Parse("#F0D285"), Color.Parse("#E3BE62")),
        ModuleStatus.Failed => (Color.Parse("#F5C4C0"), Color.Parse("#E4A5A0"), Color.Parse("#D68E88")),
        _ => (Color.Parse("#F1ECE2"), Color.Parse("#E1DACB"), Color.Parse("#D2C9B8"))
    };

    private static Color GetHealthAccentColor(NodeHealthStatus health) => health switch
    {
        NodeHealthStatus.Healthy => HighlightGreen,
        NodeHealthStatus.Processing => Color.Parse("#2B7DE9"),
        NodeHealthStatus.Suspected => WarningAmber,
        NodeHealthStatus.Failed => FailedCoral,
        NodeHealthStatus.Recovering => Color.Parse("#8A5CF6"),
        _ => MutedInkColor
    };

    private static Color GetModuleAccentColor(ModuleStatus status) => status switch
    {
        ModuleStatus.Healthy => HighlightGreen,
        ModuleStatus.Processing => Color.Parse("#2B7DE9"),
        ModuleStatus.Queued => WarningAmber,
        ModuleStatus.Suspected => WarningAmber,
        ModuleStatus.Failed => FailedCoral,
        ModuleStatus.Recovering => Color.Parse("#8A5CF6"),
        _ => MutedInkColor
    };

    private static string FormatHealthPill(NodeHealthStatus health) => health switch
    {
        NodeHealthStatus.Healthy => "Healthy",
        NodeHealthStatus.Processing => "Processing",
        NodeHealthStatus.Suspected => "Suspected",
        NodeHealthStatus.Failed => "Failed",
        NodeHealthStatus.Recovering => "Recovering",
        _ => "Idle"
    };

    private static string FormatModuleShortStatus(ModuleStatus status) => status switch
    {
        ModuleStatus.Healthy => "Healthy",
        ModuleStatus.Processing => "Processing",
        ModuleStatus.Queued => "Queued",
        ModuleStatus.Suspected => "Suspected",
        ModuleStatus.Failed => "Failed",
        ModuleStatus.Recovering => "Recovering",
        _ => "Idle"
    };

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        var nextZoom = Math.Clamp(_zoom * factor, 0.45, 2.2);
        SetZoom(nextZoom, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var screenPos = e.GetPosition(this);
        var worldPos = ScreenToWorld(screenPos);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            BeginPan(e, screenPos);
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            var hit = HitTest(worldPos);
            if (hit is not null)
            {
                var now = DateTime.UtcNow;
                var isDoubleClick = _lastClickedId == hit.Id && (now - _lastClickTime).TotalMilliseconds < 380;
                _lastClickedId = hit.Id;
                _lastClickTime = now;

                if (hit.IsModule)
                {
                    ModuleSelected?.Invoke(hit.Id);
                }
                else
                {
                    NodeSelected?.Invoke(hit.Id);
                    if (isDoubleClick)
                    {
                        NodeDoubleClicked?.Invoke(hit.Id);
                    }
                }

                e.Handled = true;
            }
            else
            {
                BeginPan(e, screenPos);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screenPos = e.GetPosition(this);

        if (_isPanning)
        {
            var delta = screenPos - _panStartScreen;
            _panOffset = new Point(_panStartOffset.X + delta.X, _panStartOffset.Y + delta.Y);
            InvalidateVisual();
            return;
        }

        var hit = HitTest(ScreenToWorld(screenPos));
        var nextHover = hit?.Id;
        if (nextHover != _hoveredId)
        {
            _hoveredId = nextHover;
            Cursor = nextHover is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
        }
    }

    private void BeginPan(PointerPressedEventArgs e, Point screenPos)
    {
        _isPanning = true;
        _panStartScreen = screenPos;
        _panStartOffset = _panOffset;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private HitTarget? HitTest(Point worldPos)
    {
        for (var i = _hitTargets.Count - 1; i >= 0; i--)
        {
            if (_hitTargets[i].Bounds.Contains(worldPos))
            {
                return _hitTargets[i];
            }
        }

        return null;
    }

    private sealed record HitTarget(string Id, bool IsModule, Rect Bounds);
}
