using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;

namespace CodexBar.App;

/// <summary>
/// The conversation in full, as the notch itself: opening it, the notch's black shape slides
/// along the top edge to the right (or left) corner and grows down into a chat as tall as the
/// screen, flat against the screen edges with one rounded corner; closing it plays the same
/// motion back into the notch. The window never resizes during the motion (only moves), and the
/// shape inside it springs to size, the same way the notch animates.
///
/// The host (MainWindow) owns the conversation and renders into <see cref="Conversation"/>; the
/// panel only reports what the user does.
/// </summary>
public partial class ConversationPanel : Window
{
    private const double NarrowWidth = 420;
    private const double MinWidth = 360;
    private const double ShadowRoom = 16;

    private sealed class Spring
    {
        public double Value, Velocity, Target;

        // Critically damped by default: the shape arrives without overshoot (no wobble).
        public double Response = 0.42, Damping = 1.0;

        public void Snap(double value)
        {
            Value = Target = value;
            Velocity = 0;
        }

        public void Step(double dt)
        {
            var omega = 2 * Math.PI / Response;
            Velocity += ((-omega * omega * (Value - Target)) - (2 * Damping * omega * Velocity)) * dt;
            Value += Velocity * dt;
        }

        public bool Settled(double tolerance) => Math.Abs(Value - Target) < tolerance && Math.Abs(Velocity) < tolerance * 12;
    }

    // Window x, the shape's offset from the top, its size, and its four corner radii.
    private readonly Spring _left = new(), _top = new(), _width = new(), _height = new();
    private readonly Spring _tl = new(), _tr = new(), _br = new(), _bl = new();
    private Spring[] Springs => [_left, _top, _width, _height, _tl, _tr, _br, _bl];

    private bool _animating;
    private TimeSpan _lastFrame;
    private bool _closing;
    private bool _allowClose;
    private double _panelWidth = NarrowWidth;
    private bool _resizing;
    private bool _dragging;
    private bool _busy;
    private bool _rightSide = true;
    private Rect _area;
    private (Rect Bounds, CornerRadius Corners) _origin;
    private int _attachmentCount;
    private double _content;

    public ConversationPanel()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => HideFromAltTab();
        PanelConversation.ScrollChanged += (sender, e) => OutputScrollChanged?.Invoke(sender, e);
        SetBusy(false);
    }

    public event ScrollChangedEventHandler? OutputScrollChanged;
    public event EventHandler? JumpLatestRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler<string>? SendRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? AttachRequested;

    /// <summary>The person removed an attached file (its path).</summary>
    public event EventHandler<string>? RemoveAttachmentRequested;

    /// <summary>Ctrl+V in the input: attaches clipboard images/files; false lets text paste.</summary>
    public Func<bool>? PasteAttachment { get; set; }

    /// <summary>Whether offline voice is installed (the mic needs it).</summary>
    public Func<bool>? VoiceAvailable { get; set; }

    /// <summary>Turns recorded 16 kHz mono PCM into text.</summary>
    public Func<byte[], Task<string>>? Transcribe { get; set; }

    /// <summary>The line under the box: approval mode (left), model and effort (right).</summary>
    public void SetComposerInfo(string mode, string model, string effort)
    {
        ModeText.Text = mode;
        ModelText.Text = model;
        EffortText.Text = effort;
        EffortText.Visibility = effort.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Voice: click to dictate, click again to insert the text ----

    private readonly CodexBar.App.Voice.WaveRecorder _recorder = new();
    private bool _transcribing;

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transcribing)
        {
            return;
        }

        if (!_recorder.IsRecording)
        {
            if (VoiceAvailable?.Invoke() != true || Transcribe is null)
            {
                InputPlaceholder.Text = Tr.Text("Aktifkan input suara di Pengaturan dulu", "Turn on voice input in Settings first");
                return;
            }

            try
            {
                _recorder.Start();
            }
            catch (Exception)
            {
                InputPlaceholder.Text = Tr.Text("Mikrofon tidak tersedia", "No microphone available");
                return;
            }

            MicGlyph.Stroke = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
            InputPlaceholder.Text = Tr.Text("Mendengarkan… klik mic lagi untuk selesai", "Listening… click the mic again to finish");
            return;
        }

        var pcm = _recorder.Stop();
        MicGlyph.Stroke = (Brush)FindResource("PanelText2");
        _transcribing = true;
        InputPlaceholder.Text = Tr.Text("Menulis…", "Transcribing…");
        try
        {
            var text = (await Transcribe!(pcm)).Trim();
            if (text.Length > 0)
            {
                var caret = Input.CaretIndex;
                var spacer = caret > 0 && !char.IsWhiteSpace(Input.Text[caret - 1]) ? " " : string.Empty;
                Input.Text = Input.Text.Insert(caret, spacer + text);
                Input.CaretIndex = caret + spacer.Length + text.Length;
                Input.Focus();
            }
        }
        catch (Exception)
        {
            // The placeholder resets below; voice failing must not break typing.
        }
        finally
        {
            _transcribing = false;
            SetBusy(_busy);
        }
    }

    /// <summary>The chat finished folding back into the notch (and is hidden now).</summary>
    public event EventHandler? Dismissed;

    /// <summary>The opening motion finished (the content is fully shown).</summary>
    public event EventHandler? Opened;

    /// <summary>True while the chat is growing out of the notch.</summary>
    public bool IsOpening => _animating && !_closing;

    /// <summary>Where the notch's shape is now (screen DIPs) and how its corners are rounded; the chat grows out of and back into it.</summary>
    public Func<(Rect Bounds, CornerRadius Corners)?>? NotchRect { get; set; }

    /// <summary>"right", "left", or "auto" (the side nearer to the notch; right when it is centred).</summary>
    public string Side { get; set; } = "auto";

    public ConversationView Conversation => PanelConversation;

    public void PrepareForExit()
    {
        _allowClose = true;
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }
    }

    /// <summary>The chat is always the notch's black; kept for the host's theme calls.</summary>
    public void ApplyBackdrop(bool dark) { }

    /// <summary>Open, growing out of the notch (anchor kept for API compatibility).</summary>
    public void ShowNear(Window? anchor)
    {
        if (IsVisible && !_closing)
        {
            return;
        }

        _closing = false;
        var origin = NotchRect?.Invoke();
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        if (!IsVisible)
        {
            Left = -20000;
            Top = -20000;
            Show();
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        _area = new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY);
        _origin = origin ?? (new Rect(_area.Left + _area.Width / 2 - 75, _area.Top, 150, 32), new CornerRadius(0, 0, 14, 14));
        var start = _origin.Bounds;
        _rightSide = Side switch
        {
            "left" => false,
            "right" => true,
            _ => start.Left + start.Width / 2 >= _area.Left + _area.Width / 2 - 1
        };

        ApplySideLayout();
        Height = _area.Height;
        Top = _area.Top;

        // Start exactly on the notch's shape…
        SnapTo(start, _origin.Corners);
        // …and let everything move to the docked chat.
        AimAt(DockedBounds(), DockedCorners());
        _content = 0;
        ApplyFrame();
        StartMotion();
        Activate();
    }

    /// <summary>Fold back into the notch, then hide and tell the host.</summary>
    public void Dismiss()
    {
        if (!IsVisible || _closing)
        {
            return;
        }

        _closing = true;
        // The notch is still in place (only see-through), so its live compact shape is the target.
        var target = NotchRect?.Invoke() ?? _origin;
        foreach (var spring in Springs)
        {
            spring.Response = 0.36;
        }

        AimAt(target.Bounds, target.Corners);
        StartMotion();
    }

    private Rect DockedBounds() =>
        new(_rightSide ? _area.Right - _panelWidth : _area.Left, _area.Top, _panelWidth, _area.Height - ShadowRoom);

    // "Widen": about half the screen, within reason.
    private double WideWidth() => Math.Clamp(_area.Width * 0.5, 720, 1100);

    private double MaxWidth() => Math.Max(MinWidth, _area.Width * 0.9);

    // Flat against the top edge and the screen side, one rounded inner corner.
    private CornerRadius DockedCorners() => _rightSide ? new CornerRadius(0, 0, 0, 22) : new CornerRadius(0, 0, 22, 0);

    // The shape's screen rectangle maps onto the window (window x moves, the shape is docked to
    // the window's screen-side edge and offset from its top).
    private void SnapTo(Rect bounds, CornerRadius corners)
    {
        _left.Snap(_rightSide ? bounds.Right - Width : bounds.Left);
        _top.Snap(bounds.Top - _area.Top);
        _width.Snap(bounds.Width);
        _height.Snap(bounds.Height);
        _tl.Snap(corners.TopLeft);
        _tr.Snap(corners.TopRight);
        _br.Snap(corners.BottomRight);
        _bl.Snap(corners.BottomLeft);
    }

    private void AimAt(Rect bounds, CornerRadius corners)
    {
        _left.Target = _rightSide ? bounds.Right - Width : bounds.Left;
        _top.Target = bounds.Top - _area.Top;
        _width.Target = bounds.Width;
        _height.Target = bounds.Height;
        _tl.Target = corners.TopLeft;
        _tr.Target = corners.TopRight;
        _br.Target = corners.BottomRight;
        _bl.Target = corners.BottomLeft;
    }

    public void SetHeader(string title, string context)
    {
        TitleText.Text = title;
        ContextText.Text = context;
    }

    /// <summary>Kept for the host's status updates; the header shows the workspace only.</summary>
    public void SetContext(string workspaceName, string status) { }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        StatusDot.Fill = Frozen(busy ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x30, 0xD1, 0x58));
        SpinBusyArc(busy);
        var stop = busy && Input.Text.Trim().Length == 0;
        SendGlyph.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        StopGlyph.Visibility = stop ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = busy || Input.Text.Trim().Length > 0 || _attachmentCount > 0;
        InputPlaceholder.Text = busy ? Tr.Text("Tambahkan ke antrean…", "Add to queue…") : Tr.Text("Tanya lanjutan…", "Ask a follow-up…");
        System.Windows.Automation.AutomationProperties.SetName(SendButton, stop ? Tr.Text("Hentikan", "Stop") : Tr.Text("Kirim", "Send"));
    }

    // Attachments as chips in the composer: image thumbnails or a file pill, each with ×.
    private void SpinBusyArc(bool busy)
    {
        if (busy == (BusyArc.Visibility == Visibility.Visible))
        {
            return;
        }

        BusyArc.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            BusyRotate.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
        }
        else
        {
            BusyRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    public void SetAttachments(IReadOnlyList<(string Name, string Path)> attachments)
    {
        _attachmentCount = attachments.Count;
        AttachmentChips.Children.Clear();
        foreach (var (name, path) in attachments)
        {
            AttachmentChips.Children.Add(AttachmentChip(name, path));
        }

        AttachmentChips.Visibility = attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetBusy(_busy);
    }

    private FrameworkElement AttachmentChip(string name, string path)
    {
        var remove = new Button
        {
            Width = 18,
            Height = 18,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = Tr.Text("Hapus", "Remove"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Template = RemoveTemplate()
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, Tr.Text("Hapus lampiran", "Remove attachment") + " " + name);
        remove.Click += (_, _) => RemoveAttachmentRequested?.Invoke(this, path);

        FrameworkElement body = ImageThumb.Create(path, 52, maxWidth: 52, square: true) ?? (FrameworkElement)new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("PanelControl"),
            Padding = new Thickness(10, 0, 14, 0),
            Child = new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = (Brush)FindResource("PanelText"),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        body.ToolTip = name;

        var chip = new Grid { Margin = new Thickness(2, 6, 10, 0) };
        chip.Children.Add(body);
        chip.Children.Add(remove);
        return chip;
    }

    private static ControlTemplate RemoveTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var circle = new FrameworkElementFactory(typeof(Border));
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        circle.SetValue(Border.BackgroundProperty, Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C)));
        circle.SetValue(Border.BorderBrushProperty, Frozen(Colors.Black));
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var cross = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        cross.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,0 L 6,6 M 6,0 L 0,6"));
        cross.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Frozen(Colors.White));
        cross.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        cross.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        cross.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        circle.AppendChild(cross);
        template.VisualTree = circle;
        return template;
    }

    public void SetJumpLatestVisible(bool visible) =>
        JumpLatestButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void ScrollToEnd() => PanelConversation.ScrollToEnd();

    public void FocusInput()
    {
        Activate();
        Input.Focus();
    }

    // ---- Motion ------------------------------------------------------------------------------

    // Flat against the top edge and the screen side: rim only on the inner sides. The content
    // has the final chat size from the start and stays put while the shape opens over it like a
    // curtain, so text never re-wraps during the motion.
    private void ApplySideLayout(double? motionWidth = null)
    {
        _panelWidth = Math.Clamp(_panelWidth, MinWidth, MaxWidth());
        Width = Math.Max(_panelWidth, motionWidth ?? 0) + ShadowRoom;
        var side = _rightSide ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
        Panel.HorizontalAlignment = side;
        Panel.BorderThickness = _rightSide ? new Thickness(1, 0, 0, 1) : new Thickness(0, 0, 1, 1);
        PanelContent.HorizontalAlignment = side;
        PanelContent.VerticalAlignment = VerticalAlignment.Top;
        PanelContent.Width = _panelWidth - 1;
        ResizeGrip.HorizontalAlignment = _rightSide ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        UpdateExpandTip();
        PanelContent.Height = Math.Max(200, _area.Height - ShadowRoom - 1);
    }

    private void StartMotion()
    {
        if (_animating)
        {
            return;
        }

        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 1 / 20.0);
        _lastFrame = now;

        // Small fixed sub-steps keep the springs stable on slow frames.
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1 / 240.0)));
        foreach (var spring in Springs)
        {
            for (var i = 0; i < steps; i++)
            {
                spring.Step(dt / steps);
            }
        }

        // Opening: the content fades in only once the shape is nearly in place (steady, no
        // shimmer while it moves). Closing: it fades out first, quickly.
        var progress = Progress();
        _content = _resizing ? 1
            : _closing ? Math.Max(0, _content - dt * 9)
            : progress > 0.82 ? Math.Min(1, _content + dt * 6) : 0;
        ApplyFrame();

        var settled = _left.Settled(0.4) && _top.Settled(0.4) && _width.Settled(0.4) && _height.Settled(0.4) &&
                      (_closing || _content >= 1);
        if (!settled)
        {
            return;
        }

        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
        foreach (var spring in Springs)
        {
            spring.Snap(spring.Target);
            spring.Response = 0.42;
        }

        if (_resizing)
        {
            // Widened or narrowed: fit the window to the new width again.
            _resizing = false;
            ApplySideLayout();
            SnapTo(DockedBounds(), DockedCorners());
            ApplyFrame();
        }
        else if (_closing)
        {
            ApplyFrame();
            // Let the host show the notch first (it sits exactly under this last frame), then
            // drop the chat a moment later: no blank frame in between.
            Dismissed?.Invoke(this, EventArgs.Empty);
            var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                if (_closing)
                {
                    _closing = false;
                    Hide();
                }
            };
            hide.Start();
        }
        else
        {
            _content = 1;
            ApplyFrame();
            Opened?.Invoke(this, EventArgs.Empty);
        }
    }

    // 0 at the notch, 1 at the docked chat (by height, the dimension that changes most).
    private double Progress()
    {
        var from = _origin.Bounds.Height;
        var to = DockedBounds().Height;
        return Math.Clamp((_height.Value - from) / Math.Max(1, to - from), 0, 1);
    }

    private void ApplyFrame()
    {
        Left = _left.Value;
        Panel.Margin = new Thickness(0, Math.Max(0, _top.Value), 0, 0);
        Panel.Width = Math.Max(4, _width.Value);
        Panel.Height = Math.Max(4, _height.Value);
        Panel.CornerRadius = new CornerRadius(
            Math.Max(0, _tl.Value), Math.Max(0, _tr.Value), Math.Max(0, _br.Value), Math.Max(0, _bl.Value));
        PanelContent.Opacity = _content;
        PanelContent.IsHitTestVisible = _content > 0.5;
    }

    // ---- Events ------------------------------------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWide();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => ToggleWide();

    private bool IsWide => _panelWidth > NarrowWidth + 60;

    // Springs the chat between its narrow and wide width (the window first spans both, so it
    // only resizes before and after the motion).
    private void ToggleWide()
    {
        if (_animating || !IsVisible)
        {
            return;
        }

        var from = DockedBounds();
        _panelWidth = IsWide ? NarrowWidth : WideWidth();
        _resizing = true;
        ApplySideLayout(motionWidth: from.Width);
        SnapTo(from, DockedCorners());
        AimAt(DockedBounds(), DockedCorners());
        ApplyFrame();
        StartMotion();
    }

    private void UpdateExpandTip()
    {
        var tip = IsWide ? Tr.Text("Perkecil", "Narrow") : Tr.Text("Perlebar", "Widen");
        ExpandButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(ExpandButton, tip);
    }

    // Drag the inner edge to set any width.
    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_animating)
        {
            return;
        }

        _dragging = ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = PointToScreen(e.GetPosition(this)).X / dpi.DpiScaleX;
        _panelWidth = _rightSide ? _area.Right - x : x - _area.Left;
        ApplySideLayout();
        SnapTo(DockedBounds(), DockedCorners());
        ApplyFrame();
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ResizeGrip.ReleaseMouseCapture();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(this, EventArgs.Empty);

    private void JumpLatestButton_Click(object sender, RoutedEventArgs e) => JumpLatestRequested?.Invoke(this, EventArgs.Empty);

    private void AttachButton_Click(object sender, RoutedEventArgs e) => AttachRequested?.Invoke(this, EventArgs.Empty);

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy && Input.Text.Trim().Length == 0)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Send();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = Input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        SetBusy(_busy);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteAttachment?.Invoke() == true)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var caret = Input.CaretIndex;
            Input.Text = Input.Text.Insert(caret, Environment.NewLine);
            Input.CaretIndex = caret + Environment.NewLine.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 && _attachmentCount == 0)
        {
            return;
        }

        Input.Clear();
        SendRequested?.Invoke(this, text);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Dismiss();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    // WS_EX_TOOLWINDOW without WS_EX_APPWINDOW: not in Alt+Tab or Task View.
    private void HideFromAltTab()
    {
        const int GwlExStyle = -20;
        const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)((style | WsExToolWindow) & ~WsExAppWindow));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
