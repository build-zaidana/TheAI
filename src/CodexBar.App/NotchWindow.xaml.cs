using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Providers;
using CodexBar.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace CodexBar.App;

/// <summary>What the notch needs from the main window; the turn pipeline itself stays there.</summary>
internal interface INotchHost
{
    string ProviderName { get; }
    string WorkspaceName { get; }
    bool IsBusy { get; }
    bool HideNotchWhenIdle { get; }

    /// <summary>Screen edge the notch is docked to: top, bottom, left or right (persisted).</summary>
    string NotchEdge { get; set; }

    /// <summary>Offset of the notch centre from the centre of its edge, as a fraction of the edge length (persisted).</summary>
    double NotchOffset { get; set; }
    IReadOnlyList<PromptTemplate> Templates { get; }
    Task SubmitFromNotchAsync(string prompt);
    Task StopFromNotchAsync();
    Task<IReadOnlyList<ProviderThreadSummary>> ListRecentThreadsAsync();
    Task OpenThreadFromNotchAsync(ProviderThreadSummary thread);
    void OpenConversationWindow();
}

/// <summary>
/// Dynamic-island style surface pinned to the top centre of the screen. Three views share one
/// black shape: <c>Compact</c> (icon + status only), <c>Launcher</c> (drops down into a prompt,
/// recent threads and template chips) and <c>Activity</c> (steps and live output of the turn).
/// </summary>
public partial class NotchWindow : Window
{
    private enum NotchView { Compact, Launcher, Activity }
    private enum NotchState { Idle, Working, Done, Waiting, Error }

    private const double IdleWidth = 150;
    private const double CompactWidth = 196;
    private const double LauncherWidth = 560;
    private const double ActivityWidth = 600;
    private const int MaxSteps = 4;
    private const int MaxOutputChars = 700;

    private static readonly Brush StepActiveBrush = Frozen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly Brush StepDoneBrush = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush StepErrorBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x61));
    private static readonly Brush DotDone = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush DotIdle = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

    private readonly INotchHost _host;
    private readonly ObservableCollection<StepItem> _steps = new();
    private readonly System.Text.StringBuilder _output = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _doneTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Storyboard _spinner;

    private IReadOnlyList<ProviderThreadSummary> _recent = [];
    private NotchView _view = NotchView.Compact;
    private NotchState _state = NotchState.Idle;
    private bool _pinned;

    internal NotchWindow(INotchHost host)
    {
        _host = host;
        InitializeComponent();
        InitTone();
        InitWindowStyle();
        ApplyEdgeLayout();
        StepList.ItemsSource = _steps;

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(spin, SpinnerIndicator.Children[1]);
        Storyboard.SetTargetProperty(spin, new PropertyPath("RenderTransform.Angle"));
        _spinner = new Storyboard();
        _spinner.Children.Add(spin);

        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); Collapse(); };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_view == NotchView.Compact && _state != NotchState.Idle)
            {
                OpenActivity();
            }
        };
        _doneTimer.Tick += (_, _) =>
        {
            _doneTimer.Stop();
            if (_view == NotchView.Compact && _state is NotchState.Done or NotchState.Error)
            {
                SetState(NotchState.Idle);
            }
        };

        ApplyView(animate: false);
        ApplyState();
    }

    // ---- Public surface used by MainWindow ------------------------------------------------

    public void ShowIdle()
    {
        ApplyView(animate: false);
        UpdateVisibility();
    }

    public void ToggleLauncher()
    {
        if (_view == NotchView.Launcher && IsActive)
        {
            Collapse();
        }
        else
        {
            OpenLauncher();
        }
    }

    /// <summary>Called by the host right before a prompt is sent (from any surface).</summary>
    public void OnTurnStarted(string prompt)
    {
        _doneTimer.Stop();
        _steps.Clear();
        _output.Clear();
        OutputText.Text = string.Empty;
        OutputBox.Visibility = Visibility.Collapsed;
        ActivityPrompt.Text = prompt;
        AddStep(L("notch.step.sending"));
        SetState(NotchState.Working);
    }

    public void HandleProviderEvent(ProviderEvent providerEvent)
    {
        switch (providerEvent)
        {
            case ProviderStatusEvent status when _state == NotchState.Working || _host.IsBusy:
                AddStep(status.Message);
                break;

            case ProviderTextDeltaEvent text:
                AppendOutput(text.Delta);
                break;

            case ProviderApprovalEvent:
            case ProviderUserInputEvent:
                AddStep(L("notch.step.waiting"));
                SetState(NotchState.Waiting);
                break;

            case ProviderErrorEvent error when !error.WillRetry:
                CompleteSteps();
                AddStep(Truncate(error.Message, 120), isError: true);
                SetState(NotchState.Error);
                _doneTimer.Start();
                break;

            case ProviderCompletedEvent completed:
                CompleteSteps();
                if (completed.Status == "completed")
                {
                    AddStep(L("notch.step.done"));
                    CompleteSteps();
                    SetState(NotchState.Done);
                }
                else
                {
                    AddStep(completed.Status == "interrupted" || completed.Status == "cancelled"
                        ? L("notch.step.cancelled")
                        : completed.Status, isError: completed.Status != "interrupted" && completed.Status != "cancelled");
                    SetState(NotchState.Idle);
                }
                _doneTimer.Start();
                break;
        }
    }

    // ---- Views -----------------------------------------------------------------------------

    private async void OpenLauncher()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        var provider = _host.ProviderName;
        LauncherProviderText.Text = provider;
        LauncherPlaceholder.Text = string.Format(L("notch.launcher.placeholder"), provider);
        LauncherWorkspaceText.Text = _host.WorkspaceName;
        RecentHeader.Text = L("notch.launcher.recent");
        LauncherHint.Text = L("notch.launcher.hint");
        BuildChips();
        LauncherInput.Text = string.Empty;
        _view = NotchView.Launcher;
        ApplyView(animate: true);
        UpdateVisibility();
        Activate();
        LauncherInput.Focus();

        try
        {
            _recent = await _host.ListRecentThreadsAsync();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Notch", exception.Message);
            _recent = [];
        }

        RenderRecent();
    }

    private void OpenActivity()
    {
        _collapseTimer.Stop();
        ActivityProviderRun.Text = _host.ProviderName;
        ActivityContextRun.Text = "  " + _host.WorkspaceName;
        _view = NotchView.Activity;
        ApplyView(animate: true);
        ApplyState();
        UpdateVisibility();
    }

    private void Collapse()
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        if (_view == NotchView.Compact)
        {
            return;
        }

        _view = NotchView.Compact;
        _pinned = false;
        ApplyPin();
        ApplyView(animate: true);
        if (_state is NotchState.Done or NotchState.Error)
        {
            _doneTimer.Start();
        }
        UpdateVisibility();
    }

    // ---- Motion -----------------------------------------------------------------------------
    //
    // The window itself never resizes (resizing a layered window every frame is what made the
    // old transition stutter). Only the black Shell inside it animates: width, height and corner
    // radius on one spring-like curve, while panels cross-fade. Panels have a fixed width, so the
    // text never reflows mid-animation, and the drop shadow is dropped while moving because
    // re-rasterising a blur each frame costs more than everything else combined.

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(14.0, (d, e) =>
        {
            ((NotchWindow)d).ApplyRadius((double)e.NewValue);
        }));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private bool _island;

    // Notch: hangs from the screen edge, only the bottom corners round. Dynamic Island: a pill
    // floating just below the edge, every corner round (half the height when compact).
    private double CompactRadius => _island ? 15 : 14;
    private double ExpandedRadius => _island ? 30 : 26;

    // Notch: only the corners facing away from its edge are round. Island: all four.
    private void ApplyRadius(double radius) =>
        Shell.CornerRadius = _island ? new CornerRadius(radius)
            : _floating ? new CornerRadius(_view == NotchView.Compact ? 999 : radius)
            : Edge switch
        {
            "bottom" => new CornerRadius(radius, radius, 0, 0),
            "left" => new CornerRadius(0, radius, radius, 0),
            "right" => new CornerRadius(radius, 0, 0, radius),
            _ => new CornerRadius(0, 0, radius, radius)
        };

    /// <summary>Switch between the notch (edge-attached) and Dynamic Island (floating pill) styles.</summary>
    public void SetIsland(bool island)
    {
        if (_island == island)
        {
            return;
        }

        _island = island;
        ApplyEdgeLayout();
        ApplyView(animate: false);
    }

    private readonly System.Windows.Media.Effects.DropShadowEffect _shadow = new()
    {
        BlurRadius = 40,
        ShadowDepth = 10,
        Direction = 270,
        Opacity = 0,
        Color = Colors.Black
    };

    private NotchView _shownView = NotchView.Compact;
    private int _transitionId;
    private bool _transitioning;

    private FrameworkElement PanelFor(NotchView view) => view switch
    {
        NotchView.Launcher => LauncherPanel,
        NotchView.Activity => ActivityPanel,
        _ => CompactPanel
    };

    private double TargetWidth => _view switch
    {
        NotchView.Launcher => LauncherWidth,
        NotchView.Activity => ActivityWidth,
        _ => _state == NotchState.Idle ? IdleWidth : CompactWidth
    };

    private static double MeasureHeight(FrameworkElement panel, double width)
    {
        panel.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        return Math.Ceiling(panel.DesiredSize.Height);
    }

    private void ApplyView(bool animate)
    {
        var incoming = PanelFor(_view);
        var outgoing = PanelFor(_shownView);
        var expanded = _view != NotchView.Compact;
        incoming.Visibility = Visibility.Visible;
        double width, height;
        if (_view == NotchView.Compact)
        {
            (width, height) = CompactSize();
            if (Vertical)
            {
                CompactPanel.Height = height - CompactPanel.Margin.Top - CompactPanel.Margin.Bottom;
            }
        }
        else
        {
            width = TargetWidth;
            height = MeasureHeight(incoming, width);
        }
        var id = ++_transitionId;

        if (!animate || !IsVisible)
        {
            foreach (var panel in new[] { CompactPanel, LauncherPanel, (FrameworkElement)ActivityPanel })
            {
                panel.BeginAnimation(OpacityProperty, null);
                panel.Opacity = 1;
                panel.Visibility = panel == incoming ? Visibility.Visible : Visibility.Collapsed;
            }

            Shell.BeginAnimation(WidthProperty, null);
            Shell.BeginAnimation(HeightProperty, null);
            BeginAnimation(RadiusProperty, null);
            Shell.Width = width;
            Shell.Height = height;
            Radius = expanded ? ExpandedRadius : CompactRadius;
            Shell.Effect = expanded ? _shadow : null;
            _shadow.Opacity = expanded ? 0.45 : 0;
            _shownView = _view;
            _transitioning = false;
            UpdateWindowStyle();
            return;
        }

        var growing = width > Shell.ActualWidth + 0.5 || height > Shell.ActualHeight + 0.5;
        var duration = TimeSpan.FromMilliseconds(growing ? 440 : 300);
        IEasingFunction ease = growing
            ? new BackEase { Amplitude = 0.16, EasingMode = EasingMode.EaseOut }
            : new QuinticEase { EasingMode = EasingMode.EaseOut };

        _transitioning = true;
        Shell.Effect = null;

        Animate(Shell, WidthProperty, Shell.ActualWidth, width, duration, ease);
        Animate(this, RadiusProperty, Radius, expanded ? ExpandedRadius : CompactRadius, duration, ease);
        Animate(Shell, HeightProperty, Shell.ActualHeight, height, duration, ease, (_, _) =>
        {
            if (id != _transitionId)
            {
                return;
            }

            _transitioning = false;
            if (_view != NotchView.Compact)
            {
                Shell.Effect = _shadow;
                _shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(0, 0.45, TimeSpan.FromMilliseconds(180)));
            }

            // Content may have changed while we were moving (recent threads arriving, etc.).
            SyncHeightToContent(TimeSpan.FromMilliseconds(180));
        });

        if (outgoing != incoming)
        {
            var fadeOut = Frame(new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
            fadeOut.Completed += (_, _) =>
            {
                if (PanelFor(_view) != outgoing)
                {
                    outgoing.Visibility = Visibility.Collapsed;
                }
            };
            outgoing.BeginAnimation(OpacityProperty, fadeOut);

            incoming.Opacity = 0;
            incoming.BeginAnimation(OpacityProperty, Frame(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                BeginTime = TimeSpan.FromMilliseconds(expanded ? 90 : 60),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }));

            if (incoming.RenderTransform is TranslateTransform slide)
            {
                slide.BeginAnimation(TranslateTransform.YProperty, Frame(new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(380))
                {
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
                }));
            }
        }

        _shownView = _view;
        UpdateWindowStyle();
    }

    // ---- Window style ----------------------------------------------------------------------
    //
    // WS_EX_TOOLWINDOW (and no WS_EX_APPWINDOW) keeps the notch out of Alt+Tab and Task View.
    // Only the black shape takes the mouse; the transparent rest of the window lets clicks through.

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExAppWindow = 0x40000;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void InitWindowStyle() => SourceInitialized += (_, _) => UpdateWindowStyle();

    private void UpdateWindowStyle()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        var wanted = (style | WsExToolWindow) & ~(WsExAppWindow | WsExTransparent);
        if (wanted != style)
        {
            SetWindowLongPtr(hwnd, GwlExStyle, (nint)wanted);
        }
    }

    // ---- Docking & dragging -------------------------------------------------------------------
    //
    // The notch lives on one screen edge (top, bottom, left or right) at an offset from that
    // edge's centre. Click it to open; drag it (more than a few pixels) anywhere and on release it
    // docks to the nearest edge, re-shaping itself: rounded corners face away from the edge, the
    // outline skips the edge side, and on left/right the compact pill stands upright.

    private const double SnapDistance = 28;
    private const double DragThreshold = 4;
    private const double CompactThickness = 30;
    private bool _pressed;
    private bool _dragging;
    private System.Windows.Point _pressCursor;
    private System.Windows.Point _lastDragPoint;
    private double _pressLeft;
    private double _pressTop;

    private string Edge => _host.NotchEdge is "bottom" or "left" or "right" ? _host.NotchEdge : "top";

    private bool Vertical => Edge is "left" or "right";

    private (double Width, double Height) CompactSize()
    {
        var length = _state == NotchState.Idle ? IdleWidth : CompactWidth;
        return Vertical ? (CompactThickness, length) : (length, CompactThickness);
    }

    private void ApplyEdgeLayout()
    {
        var edge = Edge;
        Shell.HorizontalAlignment = edge switch
        {
            "left" => System.Windows.HorizontalAlignment.Left,
            "right" => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Center
        };
        Shell.VerticalAlignment = edge switch
        {
            "top" => VerticalAlignment.Top,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center
        };

        // Transparent margin on the open sides gives the drop shadow room.
        Root.Margin = edge switch
        {
            "bottom" => new Thickness(28, 36, 28, 0),
            "left" => new Thickness(0, 28, 36, 28),
            "right" => new Thickness(36, 28, 0, 28),
            _ => new Thickness(28, 0, 28, 36)
        };

        var gap = _island ? 5 : 0;
        Shell.Margin = edge switch
        {
            "bottom" => new Thickness(0, 0, 0, gap),
            "left" => new Thickness(gap, 0, 0, 0),
            "right" => new Thickness(0, 0, gap, 0),
            _ => new Thickness(0, gap, 0, 0)
        };

        if (Vertical)
        {
            CompactPanel.Margin = new Thickness(0, 11, 0, 11);
            CompactPanel.Width = CompactThickness;
            CompactGlyph.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            CompactGlyph.VerticalAlignment = VerticalAlignment.Top;
            CompactIndicator.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            CompactIndicator.VerticalAlignment = VerticalAlignment.Bottom;
        }
        else
        {
            CompactPanel.Margin = new Thickness(11, 0, 11, 0);
            CompactPanel.Width = double.NaN;
            CompactPanel.Height = CompactThickness;
            CompactGlyph.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            CompactGlyph.VerticalAlignment = VerticalAlignment.Center;
            CompactIndicator.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            CompactIndicator.VerticalAlignment = VerticalAlignment.Center;
        }

        ApplyOutline();
        ApplyRadius(Radius);
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragging = false;
        _pressCursor = CursorDip();
        _pressLeft = Left;
        _pressTop = Top;
        Shell.CaptureMouse();
        e.Handled = true;
    }

    private void Shell_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var cursor = CursorDip();
        var dx = cursor.X - _pressCursor.X;
        var dy = cursor.Y - _pressCursor.Y;
        if (!_dragging && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
        {
            return;
        }

        if (!_dragging)
        {
            _dragging = true;
            _collapseTimer.Stop();
            _hoverTimer.Stop();
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Shell.Cursor = System.Windows.Input.Cursors.SizeAll;
            LiftOff();
        }

        _lastDragPoint = cursor;
        if (!_liquid)
        {
            Left = _pressLeft + dx;
            Top = _pressTop + dy;
        }
    }

    private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        Shell.ReleaseMouseCapture();
        if (!_dragging)
        {
            if (_view == NotchView.Compact)
            {
                OpenFromClick();
            }
            return;
        }

        _dragging = false;
        Shell.Cursor = null;
        if (_liquid)
        {
            ReleaseLiquid(_lastDragPoint);   // last pointer position seen while dragging
        }
        else
        {
            DockNear(CursorDip());
        }
    }

    private void OpenFromClick()
    {
        if (_state == NotchState.Idle)
        {
            OpenLauncher();
        }
        else
        {
            OpenActivity();
            Activate();
        }
    }

    // Whichever edge of the work area the point is closest to, and where along it.
    private (string Edge, double Offset, bool Vertical) ChooseDock(System.Windows.Point point)
    {
        var area = WindowPositioner.GetWorkArea(this);
        var distances = new (string Edge, double Distance)[]
        {
            ("top", point.Y - area.Top),
            ("bottom", area.Bottom - point.Y),
            ("left", point.X - area.Left),
            ("right", area.Right - point.X)
        };
        var edge = distances.MinBy(d => d.Distance).Edge;
        var vertical = edge is "left" or "right";
        var length = vertical ? area.Height : area.Width;
        var fromCentre = vertical ? point.Y - (area.Top + area.Height / 2) : point.X - (area.Left + area.Width / 2);
        if (Math.Abs(fromCentre) < SnapDistance)
        {
            fromCentre = 0;
        }

        return (edge, length > 0 ? Math.Clamp(fromCentre / length, -0.45, 0.45) : 0, vertical);
    }

    // Dock to whichever edge of the work area the cursor ended up closest to.
    private void DockNear(System.Windows.Point point)
    {
        var (edge, offset, vertical) = ChooseDock(point);

        // Where the droplet is right now (window coords → screen DIPs), before re-shaping it.
        var before = Shell.TranslatePoint(new System.Windows.Point(Shell.ActualWidth / 2, Shell.ActualHeight / 2), this);
        var screenCentre = new System.Windows.Point(Left + before.X, Top + before.Y);

        _host.NotchEdge = edge;
        _host.NotchOffset = offset;
        _floating = false;
        CompactIndicator.Visibility = Visibility.Visible;
        ApplyEdgeLayout();
        ApplyView(animate: false);
        ApplyState();
        UpdateLayout();

        // The new edge moves the shape inside the window; shift the window so it doesn't jump.
        var after = Shell.TranslatePoint(new System.Windows.Point(Shell.ActualWidth / 2, Shell.ActualHeight / 2), this);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = screenCentre.X - after.X;
        Top = screenCentre.Y - after.Y;

        var along = vertical ? new Vector(0, 1) : new Vector(1, 0);
        PlaceNotch(animate: true, onArrive: () => Splash(along));
    }

    /// <summary>Put the notch back in the middle of the top edge (tray menu).</summary>
    public void Recenter()
    {
        _host.NotchEdge = "top";
        _host.NotchOffset = 0;
        ApplyEdgeLayout();
        ApplyView(animate: false);
        PlaceNotch(animate: true);
    }

    private (double Left, double Top) ComputePlacement()
    {
        var area = WindowPositioner.GetWorkArea(this);
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        const double inset = 110; // keep the shape's centre away from the corners

        var centreX = Math.Clamp(area.Left + area.Width / 2 + _host.NotchOffset * area.Width, area.Left + inset, area.Right - inset);
        var centreY = Math.Clamp(area.Top + area.Height / 2 + _host.NotchOffset * area.Height, area.Top + inset, area.Bottom - inset);
        var (left, top) = Edge switch
        {
            "bottom" => (centreX - width / 2, area.Bottom - height),
            "left" => (area.Left, centreY - height / 2),
            "right" => (area.Right - width, centreY - height / 2),
            _ => (centreX - width / 2, area.Top)
        };

        return (left, top);
    }

    private void PlaceNotch(bool animate = false, Action? onArrive = null)
    {
        var (left, top) = ComputePlacement();

        if (animate && IsVisible)
        {
            var duration = TimeSpan.FromMilliseconds(280);
            var glide = Frame(new DoubleAnimation(Left, left, duration)
                { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            if (onArrive is not null)
            {
                glide.Completed += (_, _) => onArrive();
            }
            BeginAnimation(LeftProperty, glide);
            BeginAnimation(TopProperty, Frame(new DoubleAnimation(Top, top, duration)
                { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop }));
        }
        else
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
        }

        Left = left;
        Top = top;
        if (!(animate && IsVisible))
        {
            onArrive?.Invoke();
        }
    }

    // ---- Liquid motion -----------------------------------------------------------------------
    //
    // Dragging the notch should feel like pushing a puddle of water across a table: it lifts off
    // the edge as a round droplet, stretches along the direction it moves (and thins across it,
    // roughly keeping its area), lags a little behind fast moves, and when it lands on an edge it
    // spreads along it and wobbles before settling. A damped spring drives a 2D "deformation"
    // vector toward the window's current velocity; its length is the stretch, its angle the axis.

    private const double Stiffness = 190;   // spring pull toward the target deformation
    private const double Damping = 11;      // < critical (≈27.6) → a few visible wobbles
    private const double VelocityToStretch = 1.0 / 2400;
    private const double MaxStretch = 0.42;
    private bool _floating;
    private bool _physicsOn;
    private Vector _deform;
    private Vector _deformVelocity;
    private Vector _windowVelocity;
    private System.Windows.Point _lastWindowPos;
    private TimeSpan _lastFrame;

    private const double DropletWidth = 84;
    private const double DropletHeight = 34;

    // Detach from the edge: centre the shape in the (much larger, transparent) window so it can
    // stretch in any direction without being clipped by the window bounds, round it off and —
    // when compact — pull it into a short droplet. The window is shifted by the same amount the
    // shape moved inside it, so on screen nothing jumps.
    private void LiftOff()
    {
        if (_view == NotchView.Compact)
        {
            _floating = true;
            StartLiquid();
            return;
        }

        var centre = new System.Windows.Point(Shell.ActualWidth / 2, Shell.ActualHeight / 2);
        var before = Shell.TranslatePoint(centre, this);

        _floating = true;
        Shell.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Center;
        Shell.Margin = new Thickness(0);
        Root.Margin = new Thickness(0);
        ApplyOutline();
        ApplyRadius(Radius);
        UpdateLayout();

        var after = Shell.TranslatePoint(new System.Windows.Point(Shell.ActualWidth / 2, Shell.ActualHeight / 2), this);
        var shift = before - after;
        _pressLeft += shift.X;
        _pressTop += shift.Y;
        Left += shift.X;
        Top += shift.Y;

        if (_view == NotchView.Compact)
        {
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(260);
            Animate(Shell, WidthProperty, Shell.ActualWidth, DropletWidth, duration, ease);
            Animate(Shell, HeightProperty, Shell.ActualHeight, DropletHeight, duration, ease);
            CompactGlyph.Visibility = Visibility.Collapsed;
            CompactIndicator.Visibility = Visibility.Collapsed;
        }

        StartPhysics();
    }

    private void StartPhysics()
    {
        if (_physicsOn)
        {
            return;
        }

        _physicsOn = true;
        _lastFrame = TimeSpan.Zero;
        _lastWindowPos = new System.Windows.Point(Left, Top);
        _windowVelocity = new Vector();
        CompositionTarget.Rendering += OnPhysicsFrame;
    }

    private void StopPhysics()
    {
        _physicsOn = false;
        CompositionTarget.Rendering -= OnPhysicsFrame;
        _deform = new Vector();
        _deformVelocity = new Vector();
        Shell.RenderTransform = Transform.Identity;
    }

    // Landing on an edge: a quick spread along it that the spring then wobbles back out of.
    private void Splash(Vector alongEdge)
    {
        StartPhysics();
        _deformVelocity += alongEdge * 3.2;
    }

    private void OnPhysicsFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero || now <= _lastFrame)
        {
            _lastFrame = now;   // Rendering can fire twice per frame with the same time
            return;
        }

        var dt = Math.Min((now - _lastFrame).TotalSeconds, 1.0 / 30);
        _lastFrame = now;

        var position = new System.Windows.Point(Left, Top);
        var rawVelocity = (position - _lastWindowPos) / dt;
        _lastWindowPos = position;
        _windowVelocity = (_windowVelocity * 0.55) + (rawVelocity * 0.45);

        var target = _windowVelocity * VelocityToStretch;
        if (target.Length > MaxStretch)
        {
            target *= MaxStretch / target.Length;
        }

        var acceleration = ((target - _deform) * Stiffness) - (_deformVelocity * Damping);
        _deformVelocity += acceleration * dt;
        _deform += _deformVelocity * dt;
        if (_deform.Length > MaxStretch * 1.3)
        {
            _deform *= MaxStretch * 1.3 / _deform.Length;
        }

        ApplyDeform();

        if (!_dragging && !_pressed && _windowVelocity.Length < 5 &&
            _deform.Length < 0.002 && _deformVelocity.Length < 0.02)
        {
            StopPhysics();
        }
    }

    private void ApplyDeform()
    {
        var amount = _deform.Length;
        if (amount < 0.0005)
        {
            Shell.RenderTransform = Transform.Identity;
            return;
        }

        // Stretch along the deformation axis, thin across it (≈ constant area), about the centre.
        var angle = Math.Atan2(_deform.Y, _deform.X) * 180 / Math.PI;
        var stretch = 1 + amount;
        var thin = 1 / (1 + (amount * 0.8));
        var cx = Shell.ActualWidth / 2;
        var cy = Shell.ActualHeight / 2;
        var matrix = Matrix.Identity;
        matrix.Translate(-cx, -cy);
        matrix.Rotate(-angle);
        matrix.Scale(stretch, thin);
        matrix.Rotate(angle);
        matrix.Translate(cx, cy);
        Shell.RenderTransform = new MatrixTransform(matrix);
    }

    private System.Windows.Point CursorDip()
    {
        GetCursorPos(out var cursor);
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        return fromDevice.Transform(new System.Windows.Point(cursor.X, cursor.Y));
    }

    // Steps and streamed output change the active panel's height; follow it smoothly.
    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && !_transitioning && sender == PanelFor(_view))
        {
            SyncHeightToContent(TimeSpan.FromMilliseconds(200));
        }
    }

    private void SyncHeightToContent(TimeSpan duration)
    {
        var panel = PanelFor(_view);
        if (panel == CompactPanel)
        {
            return;
        }

        var target = Math.Ceiling(panel.ActualHeight + panel.Margin.Top + panel.Margin.Bottom);
        if (Math.Abs(target - Shell.ActualHeight) < 0.5)
        {
            return;
        }

        Animate(Shell, HeightProperty, Shell.ActualHeight, target, duration,
            new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    // Completed handlers are copied into the clock when the animation starts, so they must be
    // attached before BeginAnimation â€” attaching afterwards silently never fires.
    private static DoubleAnimation Animate(
        DependencyObject target, DependencyProperty property, double from, double to,
        TimeSpan duration, IEasingFunction ease, EventHandler? completed = null)
    {
        var animation = Frame(new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        if (completed is not null)
        {
            animation.Completed += completed;
        }
        ((IAnimatable)target).BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        return animation;
    }

    private static DoubleAnimation Frame(DoubleAnimation animation)
    {
        Timeline.SetDesiredFrameRate(animation, 60);
        return animation;
    }

    private void SetState(NotchState state)
    {
        _state = state;
        ApplyState();
        if (_view == NotchView.Compact)
        {
            ApplyView(animate: true);
        }
        UpdateVisibility();
    }

    private void ApplyState()
    {
        CompactGlyph.Visibility = _state == NotchState.Idle ? Visibility.Collapsed : Visibility.Visible;
        SpinnerIndicator.Visibility = _state == NotchState.Working ? Visibility.Visible : Visibility.Collapsed;
        DoneIndicator.Visibility = _state == NotchState.Done ? Visibility.Visible : Visibility.Collapsed;
        WaitIndicator.Visibility = _state == NotchState.Waiting ? Visibility.Visible : Visibility.Collapsed;
        ErrorIndicator.Visibility = _state == NotchState.Error ? Visibility.Visible : Visibility.Collapsed;

        if (_state == NotchState.Working)
        {
            _spinner.Begin(this, isControllable: true);
        }
        else
        {
            _spinner.Stop(this);
        }

        var busy = _host.IsBusy || _state is NotchState.Working or NotchState.Waiting;
        StopGlyph.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SendGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.IsEnabled = busy || FollowUpInput.Text.Trim().Length > 0;
        System.Windows.Automation.AutomationProperties.SetName(ActionButton, busy ? L("notch.stop") : L("notch.send"));
        FollowUpPlaceholder.Text = busy ? L("notch.followUp.busy") : L("notch.followUp.idle");
    }

    private void UpdateVisibility()
    {
        var hide = _view == NotchView.Compact && _state == NotchState.Idle && _host.HideNotchWhenIdle;
        if (hide)
        {
            Hide();
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        PlaceNotch();
    }

    // ---- Outline ---------------------------------------------------------------------------
    //
    // Ours is drawn over the desktop, not cut into the panel, so pure black vanishes over dark
    // backdrops. A thin white rim keeps the silhouette readable. The notch has no rim on its top
    // edge (it meets the screen edge there); the floating island is outlined all the way round.

    private static readonly Brush OutlineBrush = Frozen(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));

    private void InitTone()
    {
        Shell.BorderBrush = OutlineBrush;
        ApplyOutline();
    }

    // No rim on the side that meets the screen edge (the island floats, so it gets all four).
    private void ApplyOutline() =>
        Shell.BorderThickness = _island || _floating ? new Thickness(1) : Edge switch
        {
            "bottom" => new Thickness(1, 1, 1, 0),
            "left" => new Thickness(0, 1, 1, 1),
            "right" => new Thickness(1, 1, 0, 1),
            _ => new Thickness(1, 0, 1, 1)
        };

    // ---- Content ---------------------------------------------------------------------------

    private void AddStep(string text, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = text.Trim();
        if (_steps.Count > 0 && _steps[^1].Text == text)
        {
            return;
        }

        CompleteSteps();
        _steps.Add(new StepItem(text, Done: false, isError));
        while (_steps.Count > MaxSteps)
        {
            _steps.RemoveAt(0);
        }
    }

    private void CompleteSteps()
    {
        for (var i = 0; i < _steps.Count; i++)
        {
            if (!_steps[i].Done && !_steps[i].IsError)
            {
                _steps[i] = _steps[i] with { Done = true };
            }
        }
    }

    private void AppendOutput(string delta)
    {
        _output.Append(delta);
        if (_output.Length > MaxOutputChars * 2)
        {
            _output.Remove(0, _output.Length - MaxOutputChars);
        }

        var text = _output.ToString();
        OutputText.Text = text.Length > MaxOutputChars ? "â€¦" + text[^MaxOutputChars..].TrimStart() : text;
        OutputBox.Visibility = Visibility.Visible;
    }

    private void RenderRecent()
    {
        var query = LauncherInput.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _recent.Take(3)
            : _recent.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 t.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(4);

        var items = matches.Select(t => new RecentItem(
            t,
            string.IsNullOrWhiteSpace(t.Title) ? L("notch.untitled") : t.Title,
            FormatAge(t.UpdatedAt),
            t.Status == "active" ? (Brush)TryFindResource("NotchAccent") : t.Status == "idle" ? DotDone : DotIdle)).ToArray();

        RecentList.ItemsSource = items;
        RecentHeader.Text = string.IsNullOrEmpty(query) ? L("notch.launcher.recent") : L("notch.launcher.matches");
        RecentHeader.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChipPanel.Visibility = string.IsNullOrEmpty(query) && ChipPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildChips()
    {
        ChipPanel.Children.Clear();
        foreach (var template in _host.Templates.Take(4))
        {
            var chip = new Button { Content = template.Name, Style = (Style)FindResource("NotchChip"), Tag = template };
            chip.Click += Chip_Click;
            ChipPanel.Children.Add(chip);
        }
    }

    private async Task SubmitAsync(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 || _host.IsBusy)
        {
            return;
        }

        OpenActivity();
        try
        {
            await _host.SubmitFromNotchAsync(prompt);
        }
        catch (Exception exception)
        {
            AddStep(Truncate(exception.Message, 120), isError: true);
            SetState(NotchState.Error);
        }
    }

    // ---- Event handlers --------------------------------------------------------------------

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_view == NotchView.Launcher && !_pinned)
        {
            Collapse();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse();
        }
    }

    private void Shell_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_view == NotchView.Compact && _state != NotchState.Idle)
        {
            _hoverTimer.Start();
        }
    }

    private void Shell_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverTimer.Stop();
        var typing = FollowUpInput.IsKeyboardFocused && FollowUpInput.Text.Length > 0;
        if (_view == NotchView.Activity && !_pinned && !typing)
        {
            _collapseTimer.Start();
        }
    }

    private void LauncherInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = LauncherInput.Text.Length > 0;
        LauncherPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        LauncherSendButton.IsEnabled = LauncherInput.Text.Trim().Length > 0 && !_host.IsBusy;
        RenderRecent();
    }

    private async void LauncherInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SubmitAsync(LauncherInput.Text);
        }
        else if (e.Key == Key.Down && RecentList.Items.Count > 0)
        {
            e.Handled = true;
            RecentList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    private async void LauncherSendButton_Click(object sender, RoutedEventArgs e) => await SubmitAsync(LauncherInput.Text);

    private async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentItem item)
        {
            return;
        }

        Collapse();
        try
        {
            await _host.OpenThreadFromNotchAsync(item.Summary);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Notch", exception.Message);
        }
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PromptTemplate template)
        {
            return;
        }

        LauncherInput.Text = PromptTemplate.Expand(template.Content, null, null);
        LauncherInput.CaretIndex = LauncherInput.Text.Length;
        LauncherInput.Focus();
    }

    private void FollowUpInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FollowUpPlaceholder.Visibility = FollowUpInput.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyState();
    }

    private async void FollowUpInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            var text = FollowUpInput.Text;
            if (!_host.IsBusy && text.Trim().Length > 0)
            {
                FollowUpInput.Clear();
                await SubmitAsync(text);
            }
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.IsBusy)
        {
            await _host.StopFromNotchAsync();
            return;
        }

        var text = FollowUpInput.Text;
        FollowUpInput.Clear();
        await SubmitAsync(text);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        ApplyPin();
    }

    private void ApplyPin()
    {
        PinGlyph.Fill = _pinned ? Brushes.White : null;
        PinButton.ToolTip = _pinned ? L("notch.unpin") : L("notch.pin");
    }

    private void OpenWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        _host.OpenConversationWindow();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        if (_state is NotchState.Done or NotchState.Error)
        {
            SetState(NotchState.Idle);
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static string L(string key) => CodexBar.Core.Localization.Instance[key];

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "â€¦";

    private static string FormatAge(DateTimeOffset updatedAt)
    {
        var age = DateTimeOffset.Now - updatedAt;
        if (age.TotalMinutes < 1) return L("notch.age.now");
        if (age.TotalHours < 1) return string.Format(L("notch.age.minutes"), (int)age.TotalMinutes);
        if (age.TotalDays < 1) return string.Format(L("notch.age.hours"), (int)age.TotalHours);
        if (age.TotalDays < 2) return L("notch.age.yesterday");
        return updatedAt.LocalDateTime.ToString("d MMM");
    }

    private sealed record RecentItem(ProviderThreadSummary Summary, string Title, string Meta, Brush Dot);

    private sealed record StepItem(string Text, bool Done, bool IsError)
    {
        public Visibility DoneVisibility => Done ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ActiveVisibility => !Done && !IsError ? Visibility.Visible : Visibility.Collapsed;
        public Brush Brush => IsError ? StepErrorBrush : Done ? StepDoneBrush : StepActiveBrush;
    }
}

/// <summary>
/// Measures children with unbounded height while reporting no height of its own, so a panel's
/// ActualHeight is its true content height even while the animated Shell is shorter. That is
/// what lets the Shell follow content growth instead of clipping it.
/// </summary>
public sealed class UnclampedGrid : Grid
{
    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var desired = base.MeasureOverride(new System.Windows.Size(constraint.Width, double.PositiveInfinity));
        return new System.Windows.Size(desired.Width, 0);
    }
}
