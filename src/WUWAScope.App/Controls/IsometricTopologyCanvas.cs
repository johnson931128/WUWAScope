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
    private const double IsoSlope = IsoTileH / IsoTileW;
    private const double DragThresholdPx = 6.0;

    private static readonly Color PaperBg = Color.Parse("#FAF6EE");
    private static readonly Color GridLineColor = Color.Parse("#EDE5D5");
    private static readonly Color InkColor = Color.Parse("#1E2430");
    private static readonly Color MutedInkColor = Color.Parse("#5E6674");
    private static readonly Color ShadowColor = Color.Parse("#201E2430");
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
    private static readonly IPen ThinInkPen = new Pen(InkBrush, 1.4, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
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
        2.3,
        dashStyle: new DashStyle([6, 4], 0),
        lineJoin: PenLineJoin.Round,
        lineCap: PenLineCap.Round);

    private readonly List<HitTarget> _hitTargets = [];
    private readonly Dictionary<string, Point> _nodeDragOffsets = [];
    private readonly Dictionary<string, Point> _moduleDragOffsets = [];

    private double _zoom = 1.0;
    private Point _panOffset = new(0, 0);
    private bool _hasAutoFit;

    // Pointer interaction state
    private bool _isPanning;
    private Point _panStartScreen;
    private Point _panStartOffset;

    private HitTarget? _pressedTarget;
    private Point _pressStartScreen;
    private Point _dragItemInitialOffset;
    private bool _isDraggingItem;

    private string? _hoveredId;
    private DateTime _lastClickTime;
    private string? _lastClickedId;
    private string? _lastActiveNodeId;

    public static readonly StyledProperty<SimulationState?> SimulationStateProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, SimulationState?>(nameof(SimulationState));

    public static readonly StyledProperty<double> VisualTimeMsProperty =
        AvaloniaProperty.Register<IsometricTopologyCanvas, double>(nameof(VisualTimeMs));

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
            VisualTimeMsProperty,
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

    public double VisualTimeMs
    {
        get => GetValue(VisualTimeMsProperty);
        set => SetValue(VisualTimeMsProperty, value);
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
            var nextId = ActiveNode?.Id;
            if (nextId != _lastActiveNodeId)
            {
                _lastActiveNodeId = nextId;
                _hoveredId = null;
                FitToView();
            }
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

    public void ResetCustomLayout()
    {
        _nodeDragOffsets.Clear();
        _moduleDragOffsets.Clear();
        FitToView();
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

        const double paddingX = 84;
        const double paddingY = 84;
        var availW = Math.Max(120, Bounds.Width - paddingX * 2);
        var availH = Math.Max(120, Bounds.Height - paddingY * 2);

        var scale = Math.Min(availW / worldBounds.Width, availH / worldBounds.Height);
        _zoom = Math.Clamp(scale, 0.52, 1.35);

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

    private Point ScreenToWorld(Point screen) =>
        new((screen.X - _panOffset.X) / _zoom, (screen.Y - _panOffset.Y) / _zoom);

    private static Point GridToWorld(double gx, double gy) =>
        new((gx - gy) * IsoTileW, (gx + gy) * IsoTileH);

    private Point GetNodeWorldCenter(NodeState node)
    {
        var basePos = GridToWorld(node.GridX, node.GridY);
        return _nodeDragOffsets.TryGetValue(node.Id, out var offset)
            ? new Point(basePos.X + offset.X, basePos.Y + offset.Y)
            : basePos;
    }

    private Point GetModuleWorldCenter(ModuleState module)
    {
        var basePos = GridToWorld(module.GridX, module.GridY);
        return _moduleDragOffsets.TryGetValue(module.Id, out var offset)
            ? new Point(basePos.X + offset.X, basePos.Y + offset.Y)
            : basePos;
    }

    private Rect ComputeCurrentWorldBounds()
    {
        var points = new List<Point>();

        if (ActiveNode is { } activeNode && activeNode.Modules.Count > 0)
        {
            var minGx = activeNode.Modules.Min(m => m.GridX) - 1.55;
            var maxGx = activeNode.Modules.Max(m => m.GridX) + 1.55;
            var minGy = activeNode.Modules.Min(m => m.GridY) - 1.45;
            var maxGy = activeNode.Modules.Max(m => m.GridY) + 1.65;

            points.Add(GridToWorld(minGx, minGy) - new Vector(0, 36));
            points.Add(GridToWorld(maxGx, minGy));
            points.Add(GridToWorld(maxGx, maxGy) + new Vector(0, 28));
            points.Add(GridToWorld(minGx, maxGy));

            foreach (var m in activeNode.Modules)
            {
                var p = GetModuleWorldCenter(m);
                points.Add(new Point(p.X - 95, p.Y - 75));
                points.Add(new Point(p.X + 95, p.Y + 95));
            }
        }
        else if (SimulationState is { } state)
        {
            foreach (var n in state.Nodes)
            {
                var p = GetNodeWorldCenter(n);
                points.Add(new Point(p.X - 110, p.Y - 98));
                points.Add(new Point(p.X + 110, p.Y + 115));
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
        const double cellH = cellW * IsoSlope;

        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y)))
        {
            var minX = topLeft.X - cellW * 4;
            var maxX = bottomRight.X + cellW * 4;
            var minY = topLeft.Y - cellH * 4;
            var maxY = bottomRight.Y + cellH * 4;

            var step = cellH * 2;

            var bMin1 = Math.Floor((minY - IsoSlope * maxX) / step) * step;
            var bMax1 = Math.Ceiling((maxY - IsoSlope * minX) / step) * step;
            for (var b = bMin1; b <= bMax1; b += step)
            {
                ctx.DrawLine(GridPen, new Point(minX, IsoSlope * minX + b), new Point(maxX, IsoSlope * maxX + b));
            }

            var bMin2 = Math.Floor((minY + IsoSlope * minX) / step) * step;
            var bMax2 = Math.Ceiling((maxY + IsoSlope * maxX) / step) * step;
            for (var b = bMin2; b <= bMax2; b += step)
            {
                ctx.DrawLine(GridPen, new Point(minX, -IsoSlope * minX + b), new Point(maxX, -IsoSlope * maxX + b));
            }
        }
    }

    private void RenderClusterView(DrawingContext ctx, SimulationState state)
    {
        var nodeCenters = state.Nodes.ToDictionary(n => n.Id, GetNodeWorldCenter);

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
                link.SourceNodeId,
                from,
                to,
                link.Label,
                link.IsHeartbeatChannel,
                isEventLink,
                linkMessages,
                VisualTimeMs > 0 ? VisualTimeMs : state.SimulatedTimeMs);
        }

        foreach (var node in state.Nodes.OrderBy(n => nodeCenters[n.Id].Y))
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
        var modCenters = node.Modules.ToDictionary(m => m.Id, GetModuleWorldCenter);

        // 1. Draw true 2.5D Isometric Parallelogram Chassis Floorplate aligned with module grid axes
        if (node.Modules.Count > 0)
        {
            DrawIsometricChassisFloorplate(ctx, node);
        }

        // 2. Draw internal directional arrows between modules
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
        foreach (var module in node.Modules.OrderBy(m => modCenters[m.Id].Y))
        {
            var center = modCenters[module.Id];
            var isSelected = module.Id == SelectedModuleId;
            var isHovered = module.Id == _hoveredId;
            var isEventModule = HighlightedEvent?.ModuleId == module.Id;

            DrawModuleBlock(ctx, module, center, isSelected || isEventModule, isHovered);
        }
    }

    private static void DrawIsometricChassisFloorplate(DrawingContext ctx, NodeState node)
    {
        // Compute tight bounding rectangle in isometric (GridX, GridY) coordinates
        var minGx = node.Modules.Min(m => m.GridX) - 1.55;
        var maxGx = node.Modules.Max(m => m.GridX) + 1.55;
        var minGy = node.Modules.Min(m => m.GridY) - 1.45;
        var maxGy = node.Modules.Max(m => m.GridY) + 1.65;

        var topCorner = GridToWorld(minGx, minGy);
        var rightCorner = GridToWorld(maxGx, minGy);
        var bottomCorner = GridToWorld(maxGx, maxGy);
        var leftCorner = GridToWorld(minGx, maxGy);
        const double plateDepth = 16.0;

        var topPts = new[] { topCorner, rightCorner, bottomCorner, leftCorner };
        var shadowPts = topPts.Select(p => new Point(p.X, p.Y + plateDepth + 10)).ToArray();

        // Soft shadow underneath isometric floorplate
        ctx.DrawGeometry(ShadowBrush, null, BuildRoundedPolygon(shadowPts, 24));

        // 3D isometric side rim
        var sideColor = node.Role == NodeRole.Coordinator
            ? Color.Parse("#E9D29E")
            : node.Role == NodeRole.Worker
                ? Color.Parse("#C4DAF7")
                : Color.Parse("#E0D7C5");

        var sideRightColor = node.Role == NodeRole.Coordinator
            ? Color.Parse("#DDBF7E")
            : node.Role == NodeRole.Worker
                ? Color.Parse("#AECBF2")
                : Color.Parse("#D1C6B1");

        var silhouettePts = new[]
        {
            topCorner,
            rightCorner,
            new Point(rightCorner.X, rightCorner.Y + plateDepth),
            new Point(bottomCorner.X, bottomCorner.Y + plateDepth),
            new Point(leftCorner.X, leftCorner.Y + plateDepth),
            leftCorner
        };
        var silhouette = BuildRoundedPolygon(silhouettePts, 22);

        using (ctx.PushGeometryClip(silhouette))
        {
            var leftRim = BuildQuad(leftCorner, bottomCorner, new Point(bottomCorner.X, bottomCorner.Y + plateDepth), new Point(leftCorner.X, leftCorner.Y + plateDepth));
            var rightRim = BuildQuad(bottomCorner, rightCorner, new Point(rightCorner.X, rightCorner.Y + plateDepth), new Point(bottomCorner.X, bottomCorner.Y + plateDepth));
            ctx.DrawGeometry(new SolidColorBrush(sideColor), null, leftRim);
            ctx.DrawGeometry(new SolidColorBrush(sideRightColor), null, rightRim);
            ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#FFFDF7")), SoftCreasePen, BuildRoundedPolygon(topPts, 22));
        }

        ctx.DrawGeometry(null, InkOutlinePen, silhouette);

        // Header pill sitting cleanly near top vertex of floorplate
        DrawPillBadge(
            ctx,
            new Point(topCorner.X, topCorner.Y - 12),
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
        const double halfH = halfW * IsoSlope;
        const double depth = 18;

        // Ground shadow
        DrawRoundedDiamond(ctx, new Point(center.X, center.Y + depth + 8), halfW + 4, halfH + 3, 14, ShadowBrush, null);

        // Exact 6-point mathematically parallel selection halo
        if (isSelected)
        {
            DrawSlabSelectionHalo(ctx, c, halfW, halfH, depth, cornerRadius: 14);
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

        // === Precision-Engineered 3D Isometric Laptop (shared hinge vertices) ===
        // Isometric unit steps along screen-width (up-right) and deck-depth (down-right)
        var hingeStart = new Point(c.X - 38, c.Y + 4);               // Back-left hinge corner
        var hingeVec = new Vector(46, -46 * IsoSlope);                // Along hinge (up-right, exact 2:1 iso angle)
        var deckVec = new Vector(38, 38 * IsoSlope);                  // Extending forward-right (exact 2:1 iso angle)
        var screenUpVec = new Vector(-4.5, -45);                      // Crisp slightly-tilted upright screen
        const double baseThick = 5.0;

        var h0 = hingeStart;
        var h1 = h0 + hingeVec;
        var f0 = h0 + deckVec;
        var f1 = h1 + deckVec;

        // Soft contact shadow directly under laptop body
        var lapShadow = BuildRoundedPolygon(
            [
                new Point(h0.X + 2, h0.Y + baseThick + 3),
                new Point(h1.X + 2, h1.Y + baseThick + 3),
                new Point(f1.X + 2, f1.Y + baseThick + 3),
                new Point(f0.X + 2, f0.Y + baseThick + 3)
            ],
            4);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#241E2430")), null, lapShadow);

        // Laptop 3D metallic base rim (front-left and front-right visible thickness)
        var deckFrontLeft = BuildQuad(h0, f0, new Point(f0.X, f0.Y + baseThick), new Point(h0.X, h0.Y + baseThick));
        var deckFrontRight = BuildQuad(f0, f1, new Point(f1.X, f1.Y + baseThick), new Point(f0.X, f0.Y + baseThick));
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#D7D1C6")), ThinInkPen, deckFrontLeft);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#C4BDB0")), ThinInkPen, deckFrontRight);

        // Laptop top keyboard deck (clean rounded isometric quad)
        var deckTop = BuildRoundedPolygon([h0, h1, f1, f0], 4.5);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#FDFBF7")), ThinInkPen, deckTop);

        // Geometrically aligned isometric keyboard keys (4 rows x 6 columns)
        var keyFill = new SolidColorBrush(Color.Parse("#DCD6CB"));
        var keyPen = new Pen(new SolidColorBrush(Color.Parse("#AFA89C")), 0.65);
        for (var row = 0; row < 4; row++)
        {
            var v0 = 0.14 + row * 0.14;
            var v1 = v0 + 0.095;
            for (var col = 0; col < 6; col++)
            {
                var u0 = 0.09 + col * 0.14;
                var u1 = u0 + 0.11;

                var kp0 = h0 + hingeVec * u0 + deckVec * v0;
                var kp1 = h0 + hingeVec * u1 + deckVec * v0;
                var kp2 = h0 + hingeVec * u1 + deckVec * v1;
                var kp3 = h0 + hingeVec * u0 + deckVec * v1;
                ctx.DrawGeometry(keyFill, keyPen, BuildQuad(kp0, kp1, kp2, kp3));
            }
        }

        // Centered isometric trackpad near front edge of deck
        var tp0 = h0 + hingeVec * 0.34 + deckVec * 0.74;
        var tp1 = h0 + hingeVec * 0.66 + deckVec * 0.74;
        var tp2 = h0 + hingeVec * 0.66 + deckVec * 0.92;
        var tp3 = h0 + hingeVec * 0.34 + deckVec * 0.92;
        ctx.DrawGeometry(
            new SolidColorBrush(Color.Parse("#EFEAE0")),
            new Pen(new SolidColorBrush(Color.Parse("#B2AAA0")), 0.9),
            BuildQuad(tp0, tp1, tp2, tp3));

        // Laptop lid outer back edge (gives real 3D thickness to the screen)
        var lidBack = new Vector(-3.5, -1.8);
        var s0 = h0;
        var s1 = h1;
        var s2 = h1 + screenUpVec;
        var s3 = h0 + screenUpVec;

        var lidSide = BuildQuad(s0, s3, s3 + lidBack, s0 + lidBack);
        var lidTop = BuildQuad(s3, s2, s2 + lidBack, s3 + lidBack);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#4B5364")), ThinInkPen, lidSide);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#5F687B")), ThinInkPen, lidTop);

        // Dark charcoal screen bezel hinged cleanly along h0 -> h1
        var bezel = BuildQuad(s0, s1, s2, s3);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#232936")), InkOutlinePen, bezel);

        // Crisp inset white inner display
        Point ScreenPt(double u, double v) => s0 + hingeVec * u + screenUpVec * v;
        var innerScreen = BuildQuad(ScreenPt(0.08, 0.12), ScreenPt(0.92, 0.12), ScreenPt(0.92, 0.88), ScreenPt(0.08, 0.88));
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#FFFEFA")), null, innerScreen);

        // Mini window chrome bar + 3 colored dots inside screen
        var topBarDividerStart = ScreenPt(0.08, 0.76);
        var topBarDividerEnd = ScreenPt(0.92, 0.76);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#E3DCCE")), 1.0), topBarDividerStart, topBarDividerEnd);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#E86E65")), null, ScreenPt(0.16, 0.82), 1.6, 1.6);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#F1BD53")), null, ScreenPt(0.24, 0.82), 1.6, 1.6);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#4CB771")), null, ScreenPt(0.32, 0.82), 1.6, 1.6);

        // Soft lilac hero strip + two clean content bars inside screen
        var heroStrip = BuildQuad(ScreenPt(0.14, 0.48), ScreenPt(0.86, 0.48), ScreenPt(0.86, 0.69), ScreenPt(0.14, 0.69));
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#C7BCF2")), null, heroStrip);

        var line1 = BuildQuad(ScreenPt(0.14, 0.34), ScreenPt(0.68, 0.34), ScreenPt(0.68, 0.41), ScreenPt(0.14, 0.41));
        var line2 = BuildQuad(ScreenPt(0.14, 0.22), ScreenPt(0.54, 0.22), ScreenPt(0.54, 0.29), ScreenPt(0.14, 0.29));
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#E5DFD3")), null, line1);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#ECE7DC")), null, line2);

        // Subtle hinge cylinder crease
        ctx.DrawLine(new Pen(InkBrush, 2.2, lineCap: PenLineCap.Round), h0 + hingeVec * 0.15, h0 + hingeVec * 0.85);

        // Top floating pill ("Client · Job Submitter")
        DrawPillBadge(ctx, new Point(c.X - 6, c.Y - 76), "Client · Job Submitter", HighlightGreen, fontSize: 12, bold: true);

        // Bottom status pill
        DrawPillBadge(
            ctx,
            new Point(c.X, c.Y + halfH + depth + 18),
            FormatHealthPill(node.Health),
            GetHealthAccentColor(node.Health),
            fontSize: 11,
            bold: false);

        _hitTargets.Add(new HitTarget(node.Id, IsModule: false, new Rect(c.X - halfW, c.Y - 84, halfW * 2, halfH * 2 + depth + 92)));
    }

    private void DrawServerCubeNode(DrawingContext ctx, NodeState node, Point center, bool isSelected, bool isHovered)
    {
        var lift = isHovered ? -4.0 : 0.0;
        var c = new Point(center.X, center.Y + lift);
        var isCoord = node.Role == NodeRole.Coordinator;
        var halfW = isCoord ? 76.0 : 66.0;
        var halfH = halfW * IsoSlope;
        var depth = isCoord ? 34.0 : 30.0;

        var (topColor, leftColor, rightColor) = GetNodePalette(node);

        // Soft ground shadow
        DrawRoundedDiamond(ctx, new Point(center.X, center.Y + depth + 10), halfW + 3, halfH + 2, 14, ShadowBrush, null);

        // Exact 6-point mathematically parallel selection halo
        if (isSelected)
        {
            DrawSlabSelectionHalo(ctx, c, halfW, halfH, depth, cornerRadius: 14);
        }

        DrawIsometricSlab(ctx, c, halfW, halfH, depth, topColor, leftColor, rightColor, cornerRadius: 14);

        if (node.Health == NodeHealthStatus.Failed)
        {
            DrawFailedIcon(ctx, new Point(c.X, c.Y - 10));
        }
        else if (isCoord)
        {
            DrawCoordinatorIcon(ctx, new Point(c.X, c.Y - 12));
        }
        else
        {
            DrawWorkerServerIcon(ctx, new Point(c.X, c.Y - 10));
        }

        DrawCenteredText(ctx, node.Name, new Point(c.X, c.Y + 12), 13.5, InkBrush, FontWeight.Bold);

        var pillText = node.Health == NodeHealthStatus.Processing && node.ActiveTaskCount > 0
            ? $"{FormatHealthPill(node.Health)} · {node.ActiveTaskCount} task"
            : FormatHealthPill(node.Health);

        DrawPillBadge(
            ctx,
            new Point(c.X, c.Y + halfH + depth + 17),
            pillText,
            GetHealthAccentColor(node.Health),
            fontSize: 11,
            bold: false);

        _hitTargets.Add(new HitTarget(node.Id, IsModule: false, new Rect(c.X - halfW, c.Y - halfH, halfW * 2, halfH * 2 + depth + 28)));
    }

    private void DrawModuleBlock(DrawingContext ctx, ModuleState module, Point center, bool isSelected, bool isHovered)
    {
        var lift = isHovered ? -3.0 : 0.0;
        var c = new Point(center.X, center.Y + lift);
        const double halfW = 70;
        const double halfH = halfW * IsoSlope;
        const double depth = 24;

        var (topColor, leftColor, rightColor) = GetModulePalette(module.Status);

        DrawRoundedDiamond(ctx, new Point(center.X, center.Y + depth + 8), halfW + 3, halfH + 2, 12, ShadowBrush, null);

        if (isSelected)
        {
            DrawSlabSelectionHalo(ctx, c, halfW, halfH, depth, cornerRadius: 12);
        }

        DrawIsometricSlab(ctx, c, halfW, halfH, depth, topColor, leftColor, rightColor, cornerRadius: 12);

        // Clean 1- or 2-line label so long names stay neatly inside the diamond face
        var displayName = WrapModuleTitle(module.Name);
        if (displayName.Contains('\n'))
        {
            var lines = displayName.Split('\n');
            DrawCenteredText(ctx, lines[0], new Point(c.X, c.Y - 11), 11.5, InkBrush, FontWeight.Bold);
            DrawCenteredText(ctx, lines[1], new Point(c.X, c.Y + 3), 11.5, InkBrush, FontWeight.Bold);
            DrawCenteredText(ctx, FormatModuleShortStatus(module.Status), new Point(c.X, c.Y + 18), 10.0, MutedInkBrush, FontWeight.SemiBold);
        }
        else
        {
            DrawCenteredText(ctx, displayName, new Point(c.X, c.Y - 4), 12.2, InkBrush, FontWeight.Bold);
            DrawCenteredText(ctx, FormatModuleShortStatus(module.Status), new Point(c.X, c.Y + 13), 10.5, MutedInkBrush, FontWeight.SemiBold);
        }

        if (!string.IsNullOrWhiteSpace(module.ActiveHint))
        {
            DrawPillBadge(
                ctx,
                new Point(c.X, c.Y + halfH + depth + 15),
                module.ActiveHint,
                GetModuleAccentColor(module.Status),
                fontSize: 10.5,
                bold: false);
        }

        _hitTargets.Add(new HitTarget(module.Id, IsModule: true, new Rect(c.X - halfW, c.Y - halfH, halfW * 2, halfH * 2 + depth + 24)));
    }

    /// <summary>
    /// Draws a mathematically parallel 6-point dashed selection ring around any 3D isometric slab.
    /// Every diagonal edge matches the exact isometric slope and corner rounding of the block.
    /// </summary>
    private static void DrawSlabSelectionHalo(
        DrawingContext ctx,
        Point c,
        double halfW,
        double halfH,
        double depth,
        double cornerRadius)
    {
        const double gap = 9.0;
        var haloW = halfW + gap;
        var haloH = halfH + gap * IsoSlope;
        // Expand vertically symmetrically around top and bottom of slab
        var haloCenter = new Point(c.X, c.Y - gap * 0.35);
        var haloDepth = depth + gap * 0.70;
        var haloGeo = BuildSlabSilhouette(haloCenter, haloW, haloH, haloDepth, cornerRadius + 3.0);
        ctx.DrawGeometry(null, SelectionHaloPen, haloGeo);
    }

    private void DrawIsometricLink(
        DrawingContext ctx,
        string linkSourceNodeId,
        Point from,
        Point to,
        string label,
        bool isHeartbeat,
        bool isHighlighted,
        IReadOnlyList<MessageState> messages,
        double renderTimeMs)
    {
        var dir = to - from;
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1)
        {
            return;
        }

        var ux = dir.X / len;
        var uy = dir.Y / len;
        var start = new Point(from.X + ux * 64, from.Y + uy * 38 + 10);
        var end = new Point(to.X - ux * 64, to.Y - uy * 38 + 10);

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

        var labelT = isHeartbeat ? 0.62 : 0.52;
        var curveLabelPos = EvalQuadratic(start, ctrl, end, labelT);

        foreach (var msg in messages)
        {
            var span = Math.Max(1.0, msg.DeliverAtMs - msg.SentAtMs);
            var progress = Math.Clamp((renderTimeMs - msg.SentAtMs) / span, 0.06, 0.94);
            var forward = string.Equals(msg.SourceNodeId, linkSourceNodeId, StringComparison.Ordinal);
            var t = forward ? progress : 1.0 - progress;

            var ptMain = EvalQuadratic(start, ctrl, end, t);
            var trailT = forward ? Math.Max(0.03, t - 0.09) : Math.Min(0.97, t + 0.09);
            var ptTrail = EvalQuadratic(start, ctrl, end, trailT);

            var dotColor = msg.IsDelayed
                ? WarningAmber
                : msg.Kind == MessageKind.Heartbeat
                    ? Color.Parse("#4B8BF5")
                    : HighlightGreen;

            var dotRadius = msg.Kind == MessageKind.Heartbeat ? 4.6 : 6.2;
            var trailRadius = msg.Kind == MessageKind.Heartbeat ? 3.6 : 4.8;
            var dotPen = new Pen(InkBrush, 1.8);

            if (msg.IsDelayed)
            {
                ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#44D9822B")), null, ptMain, dotRadius + 4.5, dotRadius + 4.5);
            }

            ctx.DrawEllipse(WhiteBrush, new Pen(new SolidColorBrush(dotColor), 2.0), ptTrail, trailRadius, trailRadius);
            ctx.DrawEllipse(new SolidColorBrush(dotColor), dotPen, ptMain, dotRadius, dotRadius);
        }

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
        // Trim smoothly outside the 70x39 module slab footprint
        var start = new Point(from.X + ux * 64, from.Y + uy * 38 + 8);
        var end = new Point(to.X - ux * 64, to.Y - uy * 38 + 8);

        var pen = isHighlighted
            ? HighlightLinkPen
            : isDashed
                ? DashedLinkPen
                : ActiveLinkPen;

        ctx.DrawLine(pen, start, end);
        DrawArrowHead(ctx, start, end, isHighlighted ? new SolidColorBrush(HighlightGreen) : (isDashed ? MutedInkBrush : InkBrush));

        // Position label cleanly at midpoint, offset perpendicular to the arrow so it never crosses the line
        var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var pillPos = new Point(mid.X, mid.Y - 16);
        DrawPillBadge(ctx, pillPos, label, HighlightGreen, fontSize: 10.2, bold: false, showDot: false);
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
        var leftFace = BuildQuad(
            new Point(center.X - halfW, center.Y),
            new Point(center.X, center.Y + halfH),
            new Point(center.X, center.Y + halfH + depth),
            new Point(center.X - halfW, center.Y + depth));

        var rightFace = BuildQuad(
            new Point(center.X, center.Y + halfH),
            new Point(center.X + halfW, center.Y),
            new Point(center.X + halfW, center.Y + depth),
            new Point(center.X, center.Y + halfH + depth));

        var silhouette = BuildSlabSilhouette(center, halfW, halfH, depth, cornerRadius);

        using (ctx.PushGeometryClip(silhouette))
        {
            ctx.DrawGeometry(new SolidColorBrush(leftColor), null, leftFace);
            ctx.DrawGeometry(new SolidColorBrush(rightColor), null, rightFace);
            DrawRoundedDiamond(ctx, center, halfW, halfH, cornerRadius, new SolidColorBrush(topColor), SoftCreasePen);
            ctx.DrawLine(SoftCreasePen, new Point(center.X, center.Y + halfH - 2), new Point(center.X, center.Y + halfH + depth));
        }

        ctx.DrawGeometry(null, InkOutlinePen, silhouette);
    }

    private static StreamGeometry BuildQuad(Point p0, Point p1, Point p2, Point p3)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(p0, true);
        g.LineTo(p1);
        g.LineTo(p2);
        g.LineTo(p3);
        g.EndFigure(true);
        return geo;
    }

    private static StreamGeometry BuildSlabSilhouette(Point c, double hw, double hh, double d, double r)
    {
        var pts = new[]
        {
            new Point(c.X, c.Y - hh),
            new Point(c.X + hw, c.Y),
            new Point(c.X + hw, c.Y + d),
            new Point(c.X, c.Y + hh + d),
            new Point(c.X - hw, c.Y + d),
            new Point(c.X - hw, c.Y)
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

        ctx.DrawGeometry(fill, pen, BuildRoundedPolygon(pts, r));
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

    private static string WrapModuleTitle(string name)
    {
        if (name.Length <= 11 || !name.Contains(' '))
        {
            return name;
        }

        var parts = name.Split(' ');
        if (parts.Length == 2)
        {
            return $"{parts[0]}\n{parts[1]}";
        }

        var mid = parts.Length / 2;
        return $"{string.Join(' ', parts.Take(mid))}\n{string.Join(' ', parts.Skip(mid))}";
    }

    private static void DrawFailedIcon(DrawingContext ctx, Point center)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#A62626")), 2.6, lineCap: PenLineCap.Round);
        const double r = 8.5;
        ctx.DrawLine(pen, new Point(center.X - r, center.Y - r), new Point(center.X + r, center.Y + r));
        ctx.DrawLine(pen, new Point(center.X + r, center.Y - r), new Point(center.X - r, center.Y + r));
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

        ctx.DrawRectangle(ShadowBrush, null, new Rect(pillRect.X, pillRect.Y + 2.5, w, h), h / 2, h / 2);
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
                _pressedTarget = hit;
                _pressStartScreen = screenPos;
                _isDraggingItem = false;
                var map = hit.IsModule ? _moduleDragOffsets : _nodeDragOffsets;
                _dragItemInitialOffset = map.TryGetValue(hit.Id, out var existing) ? existing : new Point(0, 0);
                e.Pointer.Capture(this);
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

        if (_pressedTarget is not null)
        {
            var screenDelta = screenPos - _pressStartScreen;
            var dist = Math.Sqrt(screenDelta.X * screenDelta.X + screenDelta.Y * screenDelta.Y);
            if (!_isDraggingItem && dist >= DragThresholdPx)
            {
                _isDraggingItem = true;
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }

            if (_isDraggingItem)
            {
                var worldDelta = new Point(screenDelta.X / _zoom, screenDelta.Y / _zoom);
                var nextOffset = new Point(_dragItemInitialOffset.X + worldDelta.X, _dragItemInitialOffset.Y + worldDelta.Y);
                var map = _pressedTarget.IsModule ? _moduleDragOffsets : _nodeDragOffsets;
                map[_pressedTarget.Id] = nextOffset;
                InvalidateVisual();
            }

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
            return;
        }

        if (_pressedTarget is not null)
        {
            var target = _pressedTarget;
            var wasDrag = _isDraggingItem;
            _pressedTarget = null;
            _isDraggingItem = false;
            e.Pointer.Capture(null);
            Cursor = _hoveredId is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;

            if (!wasDrag)
            {
                var now = DateTime.UtcNow;
                var isDoubleClick = _lastClickedId == target.Id && (now - _lastClickTime).TotalMilliseconds < 380;
                _lastClickedId = target.Id;
                _lastClickTime = now;

                if (target.IsModule)
                {
                    ModuleSelected?.Invoke(target.Id);
                }
                else
                {
                    NodeSelected?.Invoke(target.Id);
                    if (isDoubleClick)
                    {
                        NodeDoubleClicked?.Invoke(target.Id);
                    }
                }
            }
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
