using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;

namespace SmartMacro.App.Views;

/// <summary>
/// «Макросы» — the canvas editor's interaction layer.
///
/// Everything the user can DO to the graph directly lives here: pan, zoom, drag a box, drag
/// a link out of a port. Everything that changes the graph goes straight back into
/// <see cref="MacroEditorViewModel"/> — this class owns no model state, only the transient
/// "what is currently under the pointer" of a gesture in flight.
///
/// Coordinates: the surface is a <see cref="Canvas"/> under a scale+translate render
/// transform. Screen → canvas is <c>(screen - pan) / zoom</c> and back, done in one place
/// (<see cref="ToCanvas"/> / <see cref="ToScreen"/>) so the pointer and the boxes cannot
/// drift apart.
/// </summary>
public partial class MacrosView : UserControl
{
    /// <summary>Wheel notch → zoom factor.</summary>
    private const double ZoomStep = 1.12;

    /// <summary>Button click → zoom factor. Coarser than the wheel, on purpose.</summary>
    private const double ZoomButtonStep = 1.25;

    /// <summary>Size of the surface, and therefore how far a box may be dragged.</summary>
    private const double SurfaceExtent = 4000;

    private enum Gesture
    {
        None,
        Pan,
        DragNode,
        DragLink,
    }

    private Gesture _gesture;
    private Point _gestureOrigin;
    private double _panOriginX;
    private double _panOriginY;
    private NodeRowViewModel? _draggedNode;
    private Point _dragOffset;
    private NodeEdgeViewModel? _draggedEdge;
    private MacroEditorViewModel? _watched;

    /// <summary>
    /// <b>InitializeComponent, NOT AvaloniaXamlLoader.Load.</b> The two look equivalent and
    /// are not: Avalonia's name generator puts the <c>x:Name</c> field assignments INSIDE the
    /// generated <c>InitializeComponent</c>, so calling the loader on its own loads the XAML
    /// and leaves every named field null. This view was the first one to touch a named
    /// control, and the failure is vicious — the first null dereference lands in
    /// <c>OnDataContextChanged</c>, whose exception aborts DataContext propagation to the
    /// children, so the whole panel renders with every binding silently empty and every
    /// <c>IsVisible</c> back at its default. Every sibling view was switched over too: they
    /// have no named controls today, so the bare loader worked, but it was a trap armed for
    /// whoever added the first <c>x:Name</c>.
    /// </summary>
    public MacrosView() => InitializeComponent();

    private MacroEditorViewModel? Vm => DataContext as MacroEditorViewModel;

    // ---- library ---------------------------------------------------------------------

    private void OnNewMacroClicked(object? sender, RoutedEventArgs e) => Vm?.NewMacro();

    /// <summary>
    /// Library rows are not <c>ListBoxItem</c>s (the list is a tree of groups), so opening
    /// a macro is a plain press on the row. The per-row buttons mark their own press as
    /// handled, so «Запустить» does not also open the macro.
    /// </summary>
    private void OnMacroRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: MacroListItemViewModel item })
        {
            vm.SelectedMacro = item;
        }
    }

    private void OnRunMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            vm.Run(item);
        }
    }

    private async void OnStopMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            await vm.StopAsync(item);
        }
    }

    private async void OnDeleteMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            await vm.DeleteMacroAsync(item);
        }
    }

    // ---- triggers --------------------------------------------------------------------

    private void OnAddHotkeyTriggerClicked(object? sender, RoutedEventArgs e) =>
        Vm?.AddTrigger(MacroTriggerKind.Hotkey);

    private void OnAddProcessTriggerClicked(object? sender, RoutedEventArgs e) =>
        Vm?.AddTrigger(MacroTriggerKind.ProcessAppeared);

    private void OnRemoveTriggerClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: TriggerRowViewModel row })
        {
            vm.RemoveTrigger(row);
        }
    }

    // ---- nodes -----------------------------------------------------------------------

    private void OnAddNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroNodeKindOption option })
        {
            vm.AddNode(option.Kind);
            AddNodeButton.Flyout?.Hide();
        }
    }

    private void OnDeleteNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { SelectedNode: { } row } vm)
        {
            vm.DeleteNode(row);
        }
    }

    private void OnIssueClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: ValidationIssueViewModel issue })
        {
            vm.SelectIssue(issue);
        }
    }

    // ---- editor actions --------------------------------------------------------------

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.SaveAsync();
        }
    }

    private void OnReloadClicked(object? sender, RoutedEventArgs e) => Vm?.ReloadFromDisk();

    private void OnAutoLayoutClicked(object? sender, RoutedEventArgs e) => Vm?.AutoLayout();

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = vm.FolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"Не удалось открыть папку: {ex.Message}";
        }
    }

    // ---- zoom --------------------------------------------------------------------------

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => ZoomAboutCentre(ZoomButtonStep);

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => ZoomAboutCentre(1 / ZoomButtonStep);

    private void OnZoomResetClicked(object? sender, RoutedEventArgs e)
    {
        Vm?.ResetView();
        ApplyTransform();
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }
        var factor = e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep;
        ZoomAbout(e.GetPosition(Viewport), factor);
        e.Handled = true;
    }

    private void ZoomAboutCentre(double factor) =>
        ZoomAbout(new Point(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2), factor);

    /// <summary>Zooms while keeping the canvas point under <paramref name="pivot"/> still.</summary>
    private void ZoomAbout(Point pivot, double factor)
    {
        if (Vm is not { } vm)
        {
            return;
        }
        var anchor = ToCanvas(pivot);
        vm.Zoom *= factor;
        vm.PanX = pivot.X - (anchor.X * vm.Zoom);
        vm.PanY = pivot.Y - (anchor.Y * vm.Zoom);
        ApplyTransform();
    }

    // ---- canvas gestures -----------------------------------------------------------------

    /// <summary>
    /// A press that reached the viewport itself: the pointer is over empty canvas (a box
    /// or a control inside one would have handled it first). Left or middle both pan;
    /// left also clears the selection, which is how one gets back to the macro inspector.
    /// </summary>
    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }
        Viewport.Focus();

        var point = e.GetCurrentPoint(Viewport);
        if (point.Properties.IsLeftButtonPressed)
        {
            vm.SelectedNode = null;
            vm.CollapseNodes();
        }
        else if (!point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        BeginPan(e.GetPosition(Viewport), vm);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    /// <summary>
    /// A press on a box. Selects it and starts a move; presses on the fields of an
    /// EXPANDED box never get here, because every input control marks its own press
    /// handled.
    /// </summary>
    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: NodeRowViewModel row })
        {
            return;
        }
        Viewport.Focus();
        vm.SelectedNode = row;

        var point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var canvasPoint = ToCanvas(e.GetPosition(Viewport));
        _gesture = Gesture.DragNode;
        _draggedNode = row;
        _dragOffset = new Point(canvasPoint.X - row.X, canvasPoint.Y - row.Y);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: NodeRowViewModel row })
        {
            return;
        }
        // A second double click folds it back, so the same gesture is the toggle.
        vm.ExpandNode(row.IsExpanded ? null : row);
        e.Handled = true;
    }

    /// <summary>Starts dragging a link out of an outcome port.</summary>
    private void OnPortPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: NodeEdgeViewModel edge })
        {
            return;
        }
        if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var owner = vm.Nodes.FirstOrDefault(node => node.Edges.Contains(edge));
        if (owner is null)
        {
            return;
        }

        var index = 0;
        for (var i = 0; i < owner.Edges.Count; i++)
        {
            if (ReferenceEquals(owner.Edges[i], edge))
            {
                index = i;
                break;
            }
        }

        var port = CanvasEdgeRouter.OutcomePort(owner, index);
        _gesture = Gesture.DragLink;
        _draggedEdge = edge;
        Edges.PendingStart = new Point(port.X, port.Y);
        Edges.PendingEnd = ToCanvas(e.GetPosition(Viewport));
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is not { } vm || _gesture == Gesture.None)
        {
            return;
        }

        var screen = e.GetPosition(Viewport);
        switch (_gesture)
        {
            case Gesture.Pan:
                vm.PanX = _panOriginX + (screen.X - _gestureOrigin.X);
                vm.PanY = _panOriginY + (screen.Y - _gestureOrigin.Y);
                ApplyTransform();
                break;

            case Gesture.DragNode when _draggedNode is not null:
                var canvasPoint = ToCanvas(screen);
                vm.MoveNode(
                    _draggedNode,
                    Clamp(canvasPoint.X - _dragOffset.X, CanvasMetrics.NodeWidth),
                    Clamp(canvasPoint.Y - _dragOffset.Y, _draggedNode.LayoutHeight));
                break;

            case Gesture.DragLink:
                Edges.PendingEnd = ToCanvas(screen);
                break;

            default:
                break;
        }
    }

    private void OnViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is { } vm && _gesture == Gesture.DragLink && _draggedEdge is not null)
        {
            // Dropped on a box → wire to it. Dropped anywhere else → "end of run", which
            // is a real answer and not a cancelled gesture: it is how an outcome is
            // UNwired, and the mockup insists it must not spawn a terminal node.
            var target = NodeAt(ToCanvas(e.GetPosition(Viewport)));
            vm.RewireEdge(_draggedEdge, target?.NodeId);
        }

        _gesture = Gesture.None;
        _draggedNode = null;
        _draggedEdge = null;
        Edges.PendingStart = null;
        Edges.PendingEnd = null;
        e.Pointer.Capture(null);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                vm.CollapseNodes();
                e.Handled = true;
                break;

            case Key.Delete when vm.SelectedNode is { } selected:
                vm.DeleteNode(selected);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void BeginPan(Point screen, MacroEditorViewModel vm)
    {
        _gesture = Gesture.Pan;
        _gestureOrigin = screen;
        _panOriginX = vm.PanX;
        _panOriginY = vm.PanY;
    }

    /// <summary>Topmost box covering a canvas point, or <c>null</c> for empty canvas.</summary>
    private NodeRowViewModel? NodeAt(Point canvasPoint)
    {
        if (Vm is not { } vm)
        {
            return null;
        }
        // Reverse order: later nodes draw on top, so they win a hit.
        for (var i = vm.Nodes.Count - 1; i >= 0; i--)
        {
            var node = vm.Nodes[i];
            if (canvasPoint.X >= node.X
                && canvasPoint.X <= node.X + CanvasMetrics.NodeWidth
                && canvasPoint.Y >= node.Y
                && canvasPoint.Y <= node.Y + node.LayoutHeight)
            {
                return node;
            }
        }
        return null;
    }

    private Point ToCanvas(Point screen)
    {
        var vm = Vm;
        var zoom = vm?.Zoom ?? 1;
        var panX = vm?.PanX ?? 0;
        var panY = vm?.PanY ?? 0;
        return new Point((screen.X - panX) / zoom, (screen.Y - panY) / zoom);
    }

    /// <summary>
    /// Pushes zoom/pan onto the surface.
    ///
    /// Done in code rather than by binding a <c>TransformGroup</c> in XAML because the
    /// order matters (scale, THEN translate, so the pan stays in screen pixels) and a
    /// bound group is one refactor away from silently swapping them.
    /// </summary>
    private void ApplyTransform()
    {
        // The Surface null-check is not paranoia: this runs from OnDataContextChanged, and
        // an exception there stops the DataContext from reaching the children — a failure
        // that looks like "none of the bindings work" rather than like a crash.
        if (Vm is not { } vm || Surface is null)
        {
            return;
        }
        Surface.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(vm.Zoom, vm.Zoom),
                new TranslateTransform(vm.PanX, vm.PanY),
            },
        };
    }

    /// <summary>
    /// The transform follows the view-model rather than only the gestures, because the
    /// view-model also moves it: opening a macro resets the view, and without this the
    /// canvas would keep the previous graph's pan.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _watched = Vm;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnViewModelPropertyChanged;
        }
        ApplyTransform();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTransform();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MacroEditorViewModel.Zoom)
            or nameof(MacroEditorViewModel.PanX)
            or nameof(MacroEditorViewModel.PanY))
        {
            ApplyTransform();
        }
    }

    private static double Clamp(double value, double extent) =>
        Math.Clamp(value, 0, SurfaceExtent - extent);
}
