using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace CodexBar.App;

// Expand / collapse motion. Shape size, corner radius, the corner-L arm thicknesses and the
// window position are all driven by damped springs integrated every frame (the SwiftUI
// `.spring(response:dampingFraction:)` model) instead of fixed-duration tweens. Springs keep
// their velocity when retargeted, so opening, closing and content growing mid-flight blend into
// one continuous motion instead of restarting. Content cross-fades on top: the outgoing view
// fades and shrinks a touch, the incoming one sharpens from a blur while scaling up from the
// docked edge, slightly after the shape has started to open.
public partial class NotchWindow
{
    private sealed class Spring
    {
        public double Value;
        public double Velocity;
        public double Target;
        public double Response = 0.45;
        public double Damping = 0.85;

        public void Snap(double value)
        {
            Value = Target = value;
            Velocity = 0;
        }

        public void Step(double dt)
        {
            var omega = 2 * Math.PI / Response;
            var acceleration = (-omega * omega * (Value - Target)) - (2 * Damping * omega * Velocity);
            Velocity += acceleration * dt;
            Value += Velocity * dt;
        }

        public bool Settled(double tolerance) =>
            Math.Abs(Value - Target) < tolerance && Math.Abs(Velocity) < tolerance * 12;
    }

    private readonly Spring _shapeWidth = new();
    private readonly Spring _shapeHeight = new();
    private readonly Spring _shapeRadius = new();
    private readonly Spring _armH = new();
    private readonly Spring _armV = new();
    private readonly Spring _windowLeft = new() { Damping = 1 };
    private readonly Spring _windowTop = new() { Damping = 1 };
    private bool _motionOn;
    private bool _moveWindow;
    private TimeSpan _motionLastFrame;
    private NotchView _shownView = NotchView.Compact;

    private readonly DropShadowEffect _shadow = new()
    {
        BlurRadius = 40,
        ShadowDepth = 10,
        Direction = 270,
        Opacity = 0,
        Color = Colors.Black
    };

    private FrameworkElement PanelFor(NotchView view) => view switch
    {
        NotchView.Launcher => LauncherPanel,
        NotchView.Activity => ActivityPanel,
        NotchView.Request => RequestPanel,
        _ => CompactPanel
    };

    private double TargetWidth => _view switch
    {
        NotchView.Launcher => LauncherWidth,
        NotchView.Activity or NotchView.Request => ActivityWidth,
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

        var radius = expanded ? ExpandedRadius : CompactRadius;
        var (armH, armV) = expanded ? (height, width) : (CompactThickness, CompactThickness);
        var (left, top) = ComputePlacement(width, height);

        if (!animate || !IsVisible)
        {
            foreach (var panel in new[] { CompactPanel, LauncherPanel, (FrameworkElement)ActivityPanel, RequestPanel })
            {
                panel.BeginAnimation(OpacityProperty, null);
                panel.Opacity = 1;
                panel.Effect = null;
                ResetContentTransform(panel);
                panel.Visibility = panel == incoming ? Visibility.Visible : Visibility.Collapsed;
            }

            StopMotion();
            _shapeWidth.Snap(width);
            _shapeHeight.Snap(height);
            _shapeRadius.Snap(radius);
            _armH.Snap(armH);
            _armV.Snap(armV);
            _windowLeft.Snap(left);
            _windowTop.Snap(top);
            ApplyShape();
            Left = left;
            Top = top;
            Shell.Effect = expanded ? _shadow : null;
            _shadow.Opacity = expanded ? 0.45 : 0;
            _shownView = _view;
            UpdateWindowStyle();
            return;
        }

        // Opening: a lively spring that overshoots a hair. Closing: quicker and calmer.
        var opening = expanded && _shownView == NotchView.Compact ||
                      width > _shapeWidth.Value + 0.5 || height > _shapeHeight.Value + 0.5;
        var (response, damping) = opening ? (0.52, 0.72) : (0.36, 0.94);
        Retarget(_shapeWidth, width, response, damping);
        Retarget(_shapeHeight, height, response, damping);
        Retarget(_shapeRadius, radius, response, damping);
        Retarget(_armH, armH, response, damping);
        Retarget(_armV, armV, response, damping);
        Retarget(_windowLeft, left, response, 1);
        Retarget(_windowTop, top, response, 1);
        _moveWindow = true;
        Shell.Effect = null;   // re-rasterising the blur each frame costs more than everything else
        StartMotion();

        if (outgoing != incoming)
        {
            FadeOut(outgoing);
            FadeIn(incoming, delay: TimeSpan.FromMilliseconds(expanded ? 110 : 50), blur: expanded);
        }

        _shownView = _view;
        UpdateWindowStyle();
    }

    private static void Retarget(Spring spring, double target, double response, double damping)
    {
        spring.Target = target;
        spring.Response = response;
        spring.Damping = damping;
    }

    // Steps and streamed output change the active panel's height; let the shape follow on the
    // same spring (velocity is kept, so this blends with an expansion still in progress).
    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || sender != PanelFor(_view) || _view == NotchView.Compact)
        {
            return;
        }

        var panel = (FrameworkElement)sender;
        var height = Math.Ceiling(panel.ActualHeight + panel.Margin.Top + panel.Margin.Bottom);
        if (Math.Abs(height - _shapeHeight.Target) < 0.5)
        {
            return;
        }

        _shapeHeight.Target = height;
        _armH.Target = height;
        var (left, top) = ComputePlacement(_shapeWidth.Target, height);
        _windowLeft.Target = left;
        _windowTop.Target = top;
        if (!_motionOn)
        {
            foreach (var spring in new[] { _shapeHeight, _armH, _windowLeft, _windowTop })
            {
                spring.Response = 0.34;
                spring.Damping = spring == _windowLeft || spring == _windowTop ? 1 : 0.9;
            }
            _moveWindow = !_dragging && !_liquid;
            StartMotion();
        }
    }

    private void StartMotion()
    {
        if (_motionOn)
        {
            return;
        }

        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        BeginAnimation(RadiusProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        _motionOn = true;
        _motionLastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnMotionFrame;
    }

    private void StopMotion()
    {
        if (!_motionOn)
        {
            return;
        }

        _motionOn = false;
        CompositionTarget.Rendering -= OnMotionFrame;
    }

    private void OnMotionFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_motionLastFrame == TimeSpan.Zero || now <= _motionLastFrame)
        {
            _motionLastFrame = now;
            return;
        }

        var dt = Math.Min((now - _motionLastFrame).TotalSeconds, 1.0 / 20);
        _motionLastFrame = now;

        // Small fixed substeps keep the integration stable whatever the frame time is.
        var springs = new[] { _shapeWidth, _shapeHeight, _shapeRadius, _armH, _armV, _windowLeft, _windowTop };
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
        for (var i = 0; i < steps; i++)
        {
            foreach (var spring in springs)
            {
                spring.Step(dt / steps);
            }
        }

        if (_dragging || _liquid)
        {
            _moveWindow = false;
        }

        var settled = springs.All(s => s.Settled(0.25));
        if (settled)
        {
            foreach (var spring in springs)
            {
                spring.Snap(spring.Target);
            }
        }

        ApplyShape();
        if (_moveWindow)
        {
            Left = _windowLeft.Value;
            Top = _windowTop.Value;
        }

        if (settled)
        {
            StopMotion();
            if (_view != NotchView.Compact)
            {
                Shell.Effect = _shadow;
                _shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, 0.45, TimeSpan.FromMilliseconds(180)));
            }
        }
    }

    private void ApplyShape()
    {
        Shell.Width = Math.Max(1, _shapeWidth.Value);
        Shell.Height = Math.Max(1, _shapeHeight.Value);
        Radius = Math.Max(0, _shapeRadius.Value);
        CornerShape.ArmH = Math.Max(1, _armH.Value);
        CornerShape.ArmV = Math.Max(1, _armV.Value);
    }

    // ---- Content cross-fade -------------------------------------------------------------------

    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(110);

    private void FadeOut(FrameworkElement panel)
    {
        var fade = Frame(new DoubleAnimation(0, FadeOutDuration));
        fade.Completed += (_, _) =>
        {
            if (PanelFor(_view) != panel)
            {
                panel.Visibility = Visibility.Collapsed;
                ResetContentTransform(panel);
            }
        };
        panel.BeginAnimation(OpacityProperty, fade);
        var (scale, _) = ContentTransform(panel);
        var shrink = Frame(new DoubleAnimation(0.97, FadeOutDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    private void FadeIn(FrameworkElement panel, TimeSpan delay, bool blur)
    {
        panel.Opacity = 0;
        panel.BeginAnimation(OpacityProperty, Frame(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }));

        var (scale, slide) = ContentTransform(panel);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var grow = Frame(new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var away = EdgeAwayDirection() * 10;
        slide.BeginAnimation(TranslateTransform.XProperty, Frame(new DoubleAnimation(-away.X, 0, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease }));
        slide.BeginAnimation(TranslateTransform.YProperty, Frame(new DoubleAnimation(-away.Y, 0, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease }));

        if (blur)
        {
            var effect = new BlurEffect { Radius = 10, RenderingBias = RenderingBias.Performance };
            panel.Effect = effect;
            var sharpen = Frame(new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = ease });
            sharpen.Completed += (_, _) =>
            {
                if (panel.Effect == effect)
                {
                    panel.Effect = null;
                }
            };
            effect.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
        }
    }

    // Unit vector pointing from the docked edge into the screen (content arrives from the edge).
    private Vector EdgeAwayDirection() => Edge switch
    {
        "bottom" => new Vector(0, -1),
        "left" => new Vector(1, 0),
        "right" => new Vector(-1, 0),
        "top-left" => new Vector(0.7, 0.7),
        "top-right" => new Vector(-0.7, 0.7),
        "bottom-left" => new Vector(0.7, -0.7),
        "bottom-right" => new Vector(-0.7, -0.7),
        _ => new Vector(0, 1)
    };

    private System.Windows.Point ContentOrigin() => Edge switch
    {
        "bottom" => new System.Windows.Point(0.5, 1),
        "left" => new System.Windows.Point(0, 0.5),
        "right" => new System.Windows.Point(1, 0.5),
        "top-left" => new System.Windows.Point(0, 0),
        "top-right" => new System.Windows.Point(1, 0),
        "bottom-left" => new System.Windows.Point(0, 1),
        "bottom-right" => new System.Windows.Point(1, 1),
        _ => new System.Windows.Point(0.5, 0)
    };

    private (ScaleTransform Scale, TranslateTransform Slide) ContentTransform(FrameworkElement panel)
    {
        if (panel.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform slide] })
        {
            return (scale, slide);
        }

        var group = new TransformGroup();
        scale = new ScaleTransform();
        slide = new TranslateTransform();
        group.Children.Add(scale);
        group.Children.Add(slide);
        panel.RenderTransform = group;
        panel.RenderTransformOrigin = ContentOrigin();
        return (scale, slide);
    }

    private void ResetContentTransform(FrameworkElement panel)
    {
        var (scale, slide) = ContentTransform(panel);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
        slide.X = slide.Y = 0;
        panel.RenderTransformOrigin = ContentOrigin();
    }
}
