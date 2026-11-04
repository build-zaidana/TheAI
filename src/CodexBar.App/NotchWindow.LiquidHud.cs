using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace CodexBar.App;

// The liquid HUDs (canvas Page 2, boards 23-25): volume and brightness as G2, the icon dripped out of
// the pill's left end and the whole pill a glass the liquid fills; water as E3, a capsule glass hanging
// from the middle of the pill that fills with water. Drawn by one GooSurface over the Shell, so the
// drop and the glass can leave the pill's bounds. Top-centre island only; other docks keep the plain HUD.
public partial class NotchWindow
{
    private enum LiquidHudKind { None, Volume, Brightness, Water }

    private const double LiquidHudMargin = 60;               // room left and right of the pill for the drop and the light
    private const double G2Width = 280, G2Height = 40, G2Radius = 20;
    private const double HudDropRadius = 20, HudDropOut = 24, HudDropLow = 6, Rim = 4;
    private const double E3Width = 360, GlassWidth = 112, GlassHeight = 52, GlassOverlap = 20;

    private static readonly Color[] WhiteLiquid = [Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF7, 0xF7, 0xF9), Color.FromRgb(0xEC, 0xEC, 0xF0)];
    private static readonly Color[] WarmLiquid = [Color.FromRgb(0xFF, 0xF7, 0xDF), Color.FromRgb(0xFF, 0xED, 0xBE), Color.FromRgb(0xF2, 0xD9, 0x8F)];
    private static readonly Color[] WaterLiquid = [Color.FromRgb(0xD9, 0xF4, 0xFF), Color.FromRgb(0x8E, 0xDF, 0xFF), Color.FromRgb(0x4C, 0xC4, 0xF5)];
    private static readonly Brush OnLiquid = IslandTokens.Frozen(Color.FromArgb(0xE6, 0, 0, 0));
    private static readonly Brush OnWater = IslandTokens.Frozen(Color.FromRgb(0x0A, 0x3A, 0x55));

    private LiquidHudKind _liquidHud, _liquidHudShown;
    private Grid? _liquidRoot;
    private GooSurface? _liquidSurface;
    private Grid? _liquidLight, _liquidDark;
    private Path? _liquidIconLight, _liquidIconDark;
    private TextBlock? _liquidLabelLight, _liquidLabelDark, _liquidNumberLight, _liquidNumberDark;
    private bool _liquidMuted;
    private int _liquidGoal;

    /// <summary>The island-style liquid HUD applies only to the top-centre island.</summary>
    private bool LiquidHudAllowed => Edge == "top" && !IsCorner && !_floating;

    private double LiquidHudWidth => _liquidHud == LiquidHudKind.Water ? E3Width : G2Width;

    private void EnsureLiquidHud()
    {
        if (_liquidRoot is not null)
        {
            return;
        }

        _liquidSurface = new GooSurface { NeckSoftness = 10, ChannelSoftness = 8 };
        _liquidLight = new Grid { IsHitTestVisible = false };
        _liquidDark = new Grid { IsHitTestVisible = false };
        (_liquidIconLight, _liquidLabelLight, _liquidNumberLight) = LiquidFace(_liquidLight, IslandTokens.Text1);
        (_liquidIconDark, _liquidLabelDark, _liquidNumberDark) = LiquidFace(_liquidDark, OnLiquid);
        _liquidRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
            Visibility = Visibility.Collapsed
        };
        System.Windows.Controls.Panel.SetZIndex(_liquidRoot, 50);
        _liquidRoot.Children.Add(_liquidSurface);
        _liquidRoot.Children.Add(_liquidLight);
        _liquidRoot.Children.Add(_liquidDark);
        _liquidSurface.Frame += (_, _) => FollowLiquid();
        Root.Children.Add(_liquidRoot);
    }

    private static (Path Icon, TextBlock Label, TextBlock Number) LiquidFace(Grid layer, Brush brush)
    {
        var icon = new Path
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Stroke = brush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var label = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var number = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Right
        }.WithTabular();
        layer.Children.Add(icon);
        layer.Children.Add(label);
        layer.Children.Add(number);
        return (icon, label, number);
    }

    /// <summary>Volume and brightness (G2): returns false where the plain HUD must be used instead.</summary>
    private bool ShowLiquidLevel(LiquidHudKind kind, double fraction, bool muted)
    {
        if (!LiquidHudAllowed)
        {
            return false;
        }

        EnsureLiquidHud();
        var wasShowing = _liquidHudShown == kind && _view == NotchView.Hud;
        _liquidHud = kind;
        _liquidMuted = muted;
        LayoutLiquidHud();
        var glyph = kind == LiquidHudKind.Brightness ? Icons.Brightness : muted ? Icons.VolumeMute : Icons.Volume;
        var label = kind == LiquidHudKind.Brightness ? Tr.Text("Kecerahan", "Brightness") : muted ? Tr.Text("Dibisukan", "Muted") : "Volume";
        SetLiquidFace(glyph, label, muted ? IslandTokens.Text3 : IslandTokens.Text1, muted ? IslandTokens.Text3 : IslandTokens.Text2);
        _liquidSurface!.LiquidColors = kind == LiquidHudKind.Brightness ? WarmLiquid : WhiteLiquid;
        var level = muted ? 0 : Math.Clamp(fraction, 0, 1);
        if (wasShowing)
        {
            _liquidSurface.Level = level;
        }
        else
        {
            _liquidSurface.SnapLevel(level);
        }

        System.Windows.Automation.AutomationProperties.SetName(HudPanel, $"{label} {Math.Round(level * 100)}");
        ClearPlainHud();
        return true;
    }

    /// <summary>Water (E3): the glass fills from what it was to what it is now.</summary>
    private void ShowWaterHud(int before, int after, int goal)
    {
        if (!LiquidHudAllowed || goal <= 0)
        {
            return;
        }

        EnsureLiquidHud();
        _liquidHud = LiquidHudKind.Water;
        _liquidMuted = false;
        _liquidGoal = goal;
        LayoutLiquidHud();
        SetLiquidFace(Icons.Droplet, Tr.Text("Air minum", "Water"), IslandTokens.Text1, IslandTokens.Text2);
        _liquidSurface!.LiquidColors = WaterLiquid;
        _liquidSurface.SnapLevel(Math.Clamp(before / (double)goal, 0, 1));
        _liquidSurface.Level = Math.Clamp(after / (double)goal, 0, 1);
        System.Windows.Automation.AutomationProperties.SetName(HudPanel, string.Format(CultureInfo.CurrentCulture, "{0:N0} / {1:N0} ml", after, goal));
        ClearPlainHud();
        _liquidWanted = true;
        ShowHud(HudLong);
    }

    private void ClearPlainHud()
    {
        _capsuleWanted = false;
        HudIconFill.Data = Geometry.Empty;
        HudIconStroke.Data = Geometry.Empty;
        HudIconBattery.Visibility = Visibility.Collapsed;
        HudBar.Visibility = Visibility.Collapsed;
        HudName.Visibility = Visibility.Collapsed;
        HudValue.Visibility = Visibility.Collapsed;
    }

    private void SetLiquidFace(Geometry glyph, string label, Brush icon, Brush labelBrush)
    {
        _liquidIconLight!.Data = glyph;
        _liquidIconDark!.Data = glyph;
        _liquidIconLight.Stroke = icon;
        _liquidIconDark.Stroke = _liquidHud == LiquidHudKind.Water ? OnWater : OnLiquid;
        _liquidLabelLight!.Text = label;
        _liquidLabelDark!.Text = label;
        _liquidLabelLight.Foreground = labelBrush;
        _liquidNumberLight!.Foreground = _liquidMuted ? IslandTokens.Text3 : IslandTokens.Text1;
    }

    // Shapes in the overlay's own coordinates: the pill starts LiquidHudMargin in from its left.
    private void LayoutLiquidHud()
    {
        var x0 = LiquidHudMargin;
        var surface = _liquidSurface!;
        surface.Rim = IslandRim();
        if (_liquidHud == LiquidHudKind.Water)
        {
            var w = E3Width;
            var gx = x0 + ((w - GlassWidth) / 2);
            var gy = G2Height - GlassOverlap;
            _liquidRoot!.Width = w + (2 * LiquidHudMargin);
            _liquidRoot.Height = gy + GlassHeight + 8;
            surface.Silhouette = [new GooSurface.Shape(x0, 0, w, G2Height, G2Radius, FlatTop: !_island), new GooSurface.Shape(gx, gy, GlassWidth, GlassHeight, GlassHeight / 2)];
            surface.Channel = [new GooSurface.Shape(gx + Rim, gy + Rim, GlassWidth - (2 * Rim), GlassHeight - (2 * Rim), (GlassHeight / 2) - Rim)];
            surface.Mode = GooSurface.LiquidMode.Vertical;
            surface.Span = (gy + GlassHeight - Rim, gy + Rim);
            surface.WaveLength = 60;
            surface.WaveAmplitude = 2.5;
            Place(_liquidIconLight!, _liquidIconDark!, gx + (GlassWidth / 2) - 9, gy + (GlassHeight / 2) - 9);
            Place(_liquidLabelLight!, _liquidLabelDark!, x0 + IslandTokens.P, 12);
            PlaceNumber(_liquidRoot.Width - (x0 + w - IslandTokens.P));
        }
        else
        {
            var w = G2Width;
            var cx = x0 + G2Radius - HudDropOut;
            var cy = (G2Height / 2) + HudDropLow;
            _liquidRoot!.Width = w + (2 * LiquidHudMargin);
            _liquidRoot.Height = cy + HudDropRadius + 8;
            surface.Silhouette = [new GooSurface.Shape(x0, 0, w, G2Height, G2Radius, FlatTop: !_island), GooSurface.Shape.Circle(cx, cy, HudDropRadius)];
            // The channel is round everywhere: a capsule RIM inside the pill and the drop's own circle RIM inside.
            surface.Channel = [new GooSurface.Shape(x0 + Rim, Rim, w - (2 * Rim), G2Height - (2 * Rim), (G2Height / 2) - Rim), GooSurface.Shape.Circle(cx, cy, HudDropRadius - Rim)];
            surface.Mode = GooSurface.LiquidMode.Horizontal;
            surface.Span = (cx - HudDropRadius + Rim, x0 + w - Rim);
            surface.WaveLength = 44;
            surface.WaveAmplitude = 4;
            Place(_liquidIconLight!, _liquidIconDark!, cx - 9, cy - 9);
            Place(_liquidLabelLight!, _liquidLabelDark!, x0 + 34, 12);
            PlaceNumber(_liquidRoot.Width - (x0 + w - 14));
        }

        surface.Redraw();
    }

    private static void Place(FrameworkElement light, FrameworkElement dark, double x, double y)
    {
        light.Margin = dark.Margin = new Thickness(x, y, 0, 0);
    }

    private void PlaceNumber(double right)
    {
        _liquidNumberLight!.Margin = _liquidNumberDark!.Margin = new Thickness(0, 8, right, 0);
    }

    private void FollowLiquid()
    {
        var surface = _liquidSurface!;
        var shown = Math.Clamp(surface.ShownLevel, 0, 1);
        var text = _liquidHud == LiquidHudKind.Water
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} ml", Math.Round(shown * _liquidGoal / 10) * 10)
            : Math.Round(shown * 100).ToString(CultureInfo.CurrentCulture);
        _liquidNumberLight!.Text = text;
        _liquidNumberDark!.Text = text;
        // The goal reached: the number turns the water's colour once.
        _liquidNumberLight.Foreground = _liquidHud == LiquidHudKind.Water && surface.Level >= 1 && shown >= 0.995
            ? IslandTokens.Frozen(IslandTokens.Water)
            : _liquidMuted ? IslandTokens.Text3 : IslandTokens.Text1;
        _liquidDark!.Clip = surface.LiquidGeometry();
    }

    /// <summary>Called with every view change: the overlay shows over a settled HUD pill only.</summary>
    private void ApplyLiquidHud()
    {
        if (_liquidRoot is null)
        {
            return;
        }

        var show = _view == NotchView.Hud && _liquidHud != LiquidHudKind.None && LiquidHudAllowed;
        if (show)
        {
            _liquidRoot.Margin = new Thickness(0, Shell.Margin.Top, 0, 0);
            _liquidRoot.Visibility = Visibility.Visible;
            if (_liquidHudShown != LiquidHudKind.None)
            {
                _liquidRoot.BeginAnimation(OpacityProperty, null);
                _liquidRoot.Opacity = 1;
            }
            else
            {
                // The pill grows first; the drop drips out as it settles.
                _liquidRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = TimeSpan.FromMilliseconds(120)
                });
            }

            _liquidHudShown = _liquidHud;
        }
        else if (_liquidHudShown != LiquidHudKind.None)
        {
            _liquidHudShown = LiquidHudKind.None;
            _liquidHud = LiquidHudKind.None;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
            fade.Completed += (_, _) =>
            {
                if (_liquidHudShown == LiquidHudKind.None)
                {
                    _liquidRoot.Visibility = Visibility.Collapsed;
                }
            };
            _liquidRoot.BeginAnimation(OpacityProperty, fade);
        }
    }
}
