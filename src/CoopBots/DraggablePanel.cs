using Godot;

namespace CoopBots;

/// <summary>
/// Lets the player move a CoopBots panel by dragging its title bar. The helper is
/// a plain managed object whose lifetime is held by the Godot signals it
/// subscribes to: those subscriptions keep it alive for as long as the panel is,
/// and it lets every one of them go when the panel leaves the tree. A lobby or
/// fight that rebuilds its panel therefore gets a clean drag state, and a panel
/// torn down mid-drag cannot leave the grab stuck on.
/// </summary>
internal sealed class DraggablePanel
{
    // The pointer has to travel this far before a press counts as a drag. Without
    // it a plain click on the title would take over the combat panel's placement.
    private const float DragThreshold = 2f;

    private readonly Control _panel;
    private readonly Control _handle;
    private readonly Action? _onFirstDrag;
    private Viewport? _viewport;
    private Window? _window;
    private SceneTree? _tree;
    private bool _trackingFrame;
    private bool _dragging;
    private bool _manual;
    private Vector2 _grab;
    private Vector2 _origin;

    private DraggablePanel(Control panel, Control handle, Action? onFirstDrag)
    {
        _panel = panel;
        _handle = handle;
        _onFirstDrag = onFirstDrag;
    }

    /// <summary>
    /// Makes <paramref name="handle"/> the drag grip for <paramref name="panel"/>.
    /// Title-bar children are made click-through so the whole bar grabs, but any
    /// button or dropdown among them keeps its own input. <paramref name="onFirstDrag"/>
    /// fires once, when the first real movement crosses the drag threshold.
    /// </summary>
    internal static DraggablePanel Attach(Control panel, Control handle, Action? onFirstDrag = null)
    {
        var helper = new DraggablePanel(panel, handle, onFirstDrag);
        handle.MouseFilter = Control.MouseFilterEnum.Stop;
        handle.MouseDefaultCursorShape = Control.CursorShape.Move;
        handle.TooltipText = BotUiTheme.Chinese()
            ? "按住标题拖动面板 / Drag the title to move"
            : "Drag the title to move / 按住标题拖动面板";
        MakeClickThrough(handle);
        helper._viewport = panel.GetViewport();
        helper._window = panel.GetWindow();
        helper._tree = panel.GetTree();
        handle.GuiInput += helper.OnHandleInput;
        panel.VisibilityChanged += helper.OnVisibilityChanged;
        panel.Resized += helper.ClampIntoView;
        panel.TreeExiting += helper.OnTreeExiting;
        if (helper._viewport is not null) helper._viewport.SizeChanged += helper.ClampIntoView;
        // The drag also ends on any window focus loss, even one no frame catches.
        if (helper._window is not null) helper._window.FocusExited += helper.End;
        return helper;
    }

    // Labels and the accent bar default to swallowing clicks, which would leave
    // holes in the grip; only controls that take a click themselves are kept.
    private static void MakeClickThrough(Control control)
    {
        foreach (var child in control.GetChildren())
        {
            if (child is not Control descendant || descendant is BaseButton) continue;
            descendant.MouseFilter = Control.MouseFilterEnum.Ignore;
            MakeClickThrough(descendant);
        }
    }

    private void OnHandleInput(InputEvent @event)
    {
        // A release that lands off the handle (or with the window losing focus)
        // just leaves the button up; end on the next event rather than stay held.
        if (_dragging && !Input.IsMouseButtonPressed(MouseButton.Left)) End();
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button when button.Pressed:
                Begin();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left }:
                End();
                break;
            case InputEventMouseMotion when _dragging:
                Move();
                break;
        }
    }

    private void Begin()
    {
        if (!GodotObject.IsInstanceValid(_panel)) return;
        if (_panel.GetParent() is not Control parent) return;
        _dragging = true;
        _grab = parent.GetLocalMousePosition();
        _origin = _panel.Position;
        if (_tree is not null && !_trackingFrame)
        {
            _tree.ProcessFrame += OnProcessFrame;
            _trackingFrame = true;
        }
    }

    private void Move()
    {
        if (!GodotObject.IsInstanceValid(_panel)) { End(); return; }
        if (_panel.GetParent() is not Control parent) { End(); return; }
        var delta = parent.GetLocalMousePosition() - _grab;
        // Below the threshold the press is still a click: nothing moves, so a
        // stray pixel cannot take over the panel's automatic placement.
        if (!_manual)
        {
            if (delta.LengthSquared() < DragThreshold * DragThreshold) return;
            _manual = true;
            _onFirstDrag?.Invoke();
        }
        _panel.Position = _origin + delta;
        ClampIntoView();
    }

    private void End()
    {
        _dragging = false;
        if (_trackingFrame && _tree is not null)
        {
            _tree.ProcessFrame -= OnProcessFrame;
            _trackingFrame = false;
        }
    }

    // Only runs while a drag is live, so the panel cannot hide or the button come
    // up between two events and leave the grab stuck on.
    private void OnProcessFrame()
    {
        if (!Input.IsMouseButtonPressed(MouseButton.Left)
            || !GodotObject.IsInstanceValid(_panel)
            || !_panel.IsVisibleInTree())
            End();
    }

    private void OnVisibilityChanged()
    {
        if (!GodotObject.IsInstanceValid(_panel) || !_panel.Visible) End();
    }

    // The panel is going away: end the drag and drop every subscription so the
    // helper does not outlive what it watches.
    private void OnTreeExiting()
    {
        End();
        _handle.GuiInput -= OnHandleInput;
        _panel.VisibilityChanged -= OnVisibilityChanged;
        _panel.Resized -= ClampIntoView;
        _panel.TreeExiting -= OnTreeExiting;
        if (_viewport is not null) _viewport.SizeChanged -= ClampIntoView;
        if (_window is not null) _window.FocusExited -= End;
        _viewport = null;
        _window = null;
        _tree = null;
    }

    /// <summary>
    /// Pins the panel's corners to the visible viewport. The viewport rect and the
    /// panel rect are both taken through the canvas transform and converted into
    /// the parent's local space, so a stretched or scaled canvas clamps in the same
    /// units the panel is positioned in.
    /// </summary>
    private void ClampIntoView()
    {
        if (!GodotObject.IsInstanceValid(_panel) || !_panel.IsInsideTree()) return;
        if (_panel.GetParent() is not Control parent) return;
        var inverse = parent.GetGlobalTransformWithCanvas().AffineInverse();
        var viewport = parent.GetViewportRect();
        var min = inverse * viewport.Position;
        var max = inverse * viewport.End;
        var toParent = inverse * _panel.GetGlobalTransformWithCanvas();
        var size = toParent * _panel.Size - toParent.Origin;
        var position = _panel.Position;
        position.X = Clamp(position.X, min.X, max.X - size.X);
        position.Y = Clamp(position.Y, min.Y, max.Y - size.Y);
        _panel.Position = position;
    }

    // A panel larger than the viewport has no valid inside range; pin its
    // top-left to the viewport so its title stays reachable instead of the
    // clamp pushing it out through the top or left edge.
    private static float Clamp(float value, float min, float max) =>
        max < min ? min : Mathf.Clamp(value, min, max);
}
