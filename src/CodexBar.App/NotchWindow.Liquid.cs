using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace CodexBar.App;

// Gooey / metaball drag for the compact notch (the classic After Effects "blur + levels" liquid
// look). When lifted, the pill splits into a chain of drops strung along its length; the lead
// drop follows the cursor and every other drop follows the one before it with a little more lag,
// so fast moves pull out a comet-like tail and stopping lets everything melt into one round blob.
// Released, the drops flow to their slots along the target edge, merge back into the notch shape
// and the real notch takes over with a splash wobble.
//
// Rendering: the implicit field f = Σ rᵢ²/dᵢ² is evaluated per pixel over the drops' bounding box
// into a WriteableBitmap. The contour f = 1 is the liquid surface; (f − 1)/|∇f| approximates the
// signed distance to it, which gives an anti-aliased edge and the same thin white rim the notch has.
public partial class NotchWindow
{
    private const int DropCount = 7;
    private const double SlotRadius = 15;          // half the notch thickness: drops this size form the pill
    private const double LinkSlack = 0.8;          // max gap = (rᵢ + rᵢ₋₁)·slack, so the chain never snaps into beads
    private static readonly double[] DropRadius = { 19, 16.5, 14.5, 12.5, 11, 9.5, 8.5 };
    private static readonly double[] FollowRate = { 0, 30, 25, 21, 18, 15.5, 13.5 };

    private readonly Point[] _drops = new Point[DropCount];         // screen DIPs
    private readonly Vector[] _dropVelocity = new Vector[DropCount];
    private readonly double[] _dropRadius = new double[DropCount];
    private readonly Point[] _dropTarget = new Point[DropCount];
    private bool _liquid;
    private bool _liquidReleasing;
    private NotchView? _reopenAfterDrop;
    private Vector _grabOffset;
    private Vector _landingAlong;
    private TimeSpan _liquidLastFrame;
    private WriteableBitmap? _liquidBitmap;
    private int[] _liquidPixels = [];
    private Int32Rect _liquidLastRect = Int32Rect.Empty;

    private void StartLiquid()
    {
        var (centreInWindow, along, length) = ArmAnchor();
        var centre = new Point(Left + centreInWindow.X, Top + centreInWindow.Y);
        var cursor = CursorDip();

        // Lead drop sits where the pill was grabbed; the rest are strung along the pill, nearest first.
        var grab = Math.Clamp(Vector.Multiply(cursor - centre, along), -length / 2 + SlotRadius, length / 2 - SlotRadius);
        var slots = Enumerable.Range(0, DropCount)
            .Select(i => -length / 2 + SlotRadius + ((length - 2 * SlotRadius) * i / (DropCount - 1)))
            .OrderBy(s => Math.Abs(s - grab))
            .ToArray();
        for (var i = 0; i < DropCount; i++)
        {
            _drops[i] = centre + (along * slots[i]);
            _dropVelocity[i] = new Vector();
            _dropRadius[i] = SlotRadius;
        }

        _grabOffset = cursor - _drops[0];
        EnsureLiquidSurface();
        Shell.Opacity = 0;              // stays hit-testable, so the mouse capture survives
        LiquidImage.Visibility = Visibility.Visible;
        _liquid = true;
        _liquidReleasing = false;
        _liquidLastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnLiquidFrame;
    }

    // Mouse released: choose the edge, lay the real notch out there (still invisible) and give
    // each drop a slot along it; the drops then flow into those slots.
    private void ReleaseLiquid(Point cursor)
    {
        var (edge, offset, vertical) = ChooseDock(cursor);
        _host.NotchEdge = edge;
        _host.NotchOffset = offset;
        _floating = false;
        CompactIndicator.Visibility = Visibility.Visible;
        ApplyEdgeLayout();
        ApplyView(animate: false);
        ApplyState();
        UpdateLayout();

        var (left, top) = ComputePlacement(_shapeWidth.Target, _shapeHeight.Target);
        var (anchor, along, length) = ArmAnchor();
        var target = new Point(left + anchor.X, top + anchor.Y);

        // Keep the drops' current order along the edge so they don't cross on the way in.
        var order = Enumerable.Range(0, DropCount).OrderBy(i => Vector.Multiply(_drops[i] - target, along)).ToArray();
        for (var rank = 0; rank < DropCount; rank++)
        {
            var slot = -length / 2 + SlotRadius + ((length - 2 * SlotRadius) * rank / (DropCount - 1));
            _dropTarget[order[rank]] = target + (along * slot);
        }

        _landingAlong = along;
        _liquidReleasing = true;
    }

    private void OnLiquidFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_liquidLastFrame == TimeSpan.Zero || now <= _liquidLastFrame)
        {
            _liquidLastFrame = now;
            return;
        }

        var dt = Math.Min((now - _liquidLastFrame).TotalSeconds, 1.0 / 30);
        _liquidLastFrame = now;

        if (_liquidReleasing)
        {
            StepRelease(dt);
        }
        else
        {
            StepDrag(dt);
        }

        // The window rides along with the liquid so it never leaves the drawable area.
        var centroid = new Point(_drops.Average(p => p.X), _drops.Average(p => p.Y));
        Left = centroid.X - ActualWidth / 2;
        Top = centroid.Y - ActualHeight / 2;
        RenderLiquid();

        if (_liquidReleasing && Settled())
        {
            FinishLiquid();
        }
    }

    private void StepDrag(double dt)
    {
        _grabOffset *= Math.Exp(-9 * dt);
        var lead = CursorDip() - _grabOffset;
        _dropVelocity[0] = (lead - _drops[0]) / dt;
        _drops[0] = lead;

        for (var i = 1; i < DropCount; i++)
        {
            var k = 1 - Math.Exp(-FollowRate[i] * dt);
            var previous = _drops[i];
            _drops[i] += (_drops[i - 1] - _drops[i]) * k;
            var link = _drops[i] - _drops[i - 1];
            var maxLink = (_dropRadius[i] + _dropRadius[i - 1]) * LinkSlack;
            if (link.Length > maxLink)
            {
                _drops[i] = _drops[i - 1] + (link * (maxLink / link.Length));
            }
            _dropVelocity[i] = (_drops[i] - previous) / dt;
        }

        // Drops swell to their comet sizes once free of the pill.
        for (var i = 0; i < DropCount; i++)
        {
            _dropRadius[i] += (DropRadius[i] - _dropRadius[i]) * (1 - Math.Exp(-8 * dt));
        }
    }

    private void StepRelease(double dt)
    {
        for (var i = 0; i < DropCount; i++)
        {
            // Critically damped spring per drop; trailing drops are a touch slower so the liquid
            // pours into the slot rather than teleporting as one piece.
            var omega = 19 - (i * 1.3);
            var acceleration = ((_dropTarget[i] - _drops[i]) * (omega * omega)) - (_dropVelocity[i] * (2 * omega));
            _dropVelocity[i] += acceleration * dt;
            _drops[i] += _dropVelocity[i] * dt;
            _dropRadius[i] += (SlotRadius - _dropRadius[i]) * (1 - Math.Exp(-10 * dt));
        }
    }

    private bool Settled()
    {
        for (var i = 0; i < DropCount; i++)
        {
            if ((_dropTarget[i] - _drops[i]).Length > 1.0 || _dropVelocity[i].Length > 40 ||
                Math.Abs(_dropRadius[i] - SlotRadius) > 0.6)
            {
                return false;
            }
        }

        return true;
    }

    private void FinishLiquid()
    {
        CompositionTarget.Rendering -= OnLiquidFrame;
        _liquid = false;
        _liquidReleasing = false;
        ClearLiquid();
        LiquidImage.Visibility = Visibility.Collapsed;
        PlaceNotch();
        Shell.Opacity = 1;
        ApplyState();
        Splash(_landingAlong);

        // A notch that was open when it was picked up opens again where it landed.
        var reopen = _reopenAfterDrop;
        _reopenAfterDrop = null;
        if (reopen == NotchView.Launcher)
        {
            _view = NotchView.Launcher;
            ApplyView(animate: true);
            Activate();
            LauncherInput.Focus();
        }
        else if (reopen == NotchView.Activity)
        {
            OpenActivity();
        }
        else if (reopen is NotchView.Request or NotchView.Quick && HasPendingRequest)
        {
            ShowPendingRequest(preferQuick: reopen == NotchView.Quick);
        }
    }

    // ---- Rendering ------------------------------------------------------------------------

    private void EnsureLiquidSurface()
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        if (_liquidBitmap is { } existing && existing.PixelWidth == width && existing.PixelHeight == height)
        {
            return;
        }

        _liquidBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        _liquidPixels = new int[width * height];
        _liquidLastRect = Int32Rect.Empty;
        LiquidImage.Source = _liquidBitmap;
    }

    private void RenderLiquid()
    {
        if (_liquidBitmap is not { } bitmap)
        {
            return;
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var originX = Left;
        var originY = Top;
        Span<double> xs = stackalloc double[DropCount];
        Span<double> ys = stackalloc double[DropCount];
        Span<double> r2 = stackalloc double[DropCount];

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < DropCount; i++)
        {
            xs[i] = _drops[i].X - originX;
            ys[i] = _drops[i].Y - originY;
            r2[i] = _dropRadius[i] * _dropRadius[i];
            var reach = _dropRadius[i] * 2.2;
            minX = Math.Min(minX, xs[i] - reach);
            minY = Math.Min(minY, ys[i] - reach);
            maxX = Math.Max(maxX, xs[i] + reach);
            maxY = Math.Max(maxY, ys[i] + reach);
        }

        var x0 = Math.Clamp((int)minX, 0, width);
        var y0 = Math.Clamp((int)minY, 0, height);
        var x1 = Math.Clamp((int)Math.Ceiling(maxX), 0, width);
        var y1 = Math.Clamp((int)Math.Ceiling(maxY), 0, height);
        var rect = new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));

        // Wipe what was drawn last frame, then draw the new blob.
        ClearRect(_liquidLastRect, width);
        for (var y = y0; y < y1; y++)
        {
            var py = y + 0.5;
            var row = y * width;
            for (var x = x0; x < x1; x++)
            {
                var px = x + 0.5;
                double field = 0, gx = 0, gy = 0;
                for (var i = 0; i < DropCount; i++)
                {
                    var dx = px - xs[i];
                    var dy = py - ys[i];
                    var d2 = (dx * dx) + (dy * dy) + 0.01;
                    var term = r2[i] / d2;
                    field += term;
                    var g = 2 * term / d2;
                    gx -= g * dx;
                    gy -= g * dy;
                }

                // Deep inside (field ≫ 1) the distance estimate breaks down near a drop's centre.
                var gradient = Math.Sqrt((gx * gx) + (gy * gy));
                var distance = field > 3 ? 10 : gradient > 1e-6 ? (field - 1) / gradient : (field > 1 ? 10 : -10);
                var coverage = Math.Clamp(distance + 0.5, 0, 1);
                if (coverage <= 0)
                {
                    _liquidPixels[row + x] = 0;
                    continue;
                }

                var rim = Math.Min(Math.Clamp(1 - (Math.Abs(distance - 0.6) / 0.7), 0, 1) * 0.36, coverage);
                var alpha = (int)(coverage * 255);
                var white = (int)(rim * 255);
                _liquidPixels[row + x] = (alpha << 24) | (white << 16) | (white << 8) | white;
            }
        }

        var dirty = Union(_liquidLastRect, rect);
        if (dirty.Width > 0 && dirty.Height > 0)
        {
            bitmap.WritePixels(dirty, _liquidPixels, width * 4, dirty.X, dirty.Y);
        }

        _liquidLastRect = rect;
    }

    private void ClearLiquid()
    {
        if (_liquidBitmap is not { } bitmap || _liquidLastRect.Width == 0)
        {
            return;
        }

        ClearRect(_liquidLastRect, bitmap.PixelWidth);
        bitmap.WritePixels(_liquidLastRect, _liquidPixels, bitmap.PixelWidth * 4, _liquidLastRect.X, _liquidLastRect.Y);
        _liquidLastRect = Int32Rect.Empty;
    }

    private void ClearRect(Int32Rect rect, int stride)
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            Array.Clear(_liquidPixels, (y * stride) + rect.X, rect.Width);
        }
    }

    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (a.Width == 0 || a.Height == 0)
        {
            return b;
        }

        if (b.Width == 0 || b.Height == 0)
        {
            return a;
        }

        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x, y, right - x, bottom - y);
    }
}
