using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Rodspaette.App;

/// <summary>
/// CAD-style marking menu: a ring of four wedges (up, right, down, left) drawn around the point where the right
/// button went down. Moving into a wedge selects it; releasing the button runs it.
/// </summary>
public sealed class RadialMenu : Canvas
{
    public const double Inner = 30, Outer = 120;
    private const double DeadZone = 22;
    private const double GapDegrees = 2.2;

    private readonly Canvas _ring = new();
    private readonly Path[] _wedges = new Path[4];
    private readonly TextBlock[] _texts = new TextBlock[4];
    private readonly IconView[] _icons = new IconView[4];
    private readonly Line _ray = new() { StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private Point _centre;

    public int Selected { get; private set; } = -1;
    public bool IsOpen => Visibility == Visibility.Visible;

    public RadialMenu()
    {
        Background = Brushes.Transparent;   // catches the mouse everywhere while open
        Visibility = Visibility.Collapsed;
        _ring.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.22 };
        Children.Add(_ring);
        _ray.SetResourceReference(Shape.StrokeProperty, "B.Accent");
        Children.Add(_ray);
    }

    /// <summary>Opens at <paramref name="centre"/> with up, right, down, left slots.</summary>
    public void Show(Point centre, IReadOnlyList<(string label, string icon)> slots)
    {
        _centre = centre;
        _ring.Children.Clear();
        for (int i = 0; i < 4; i++)
        {
            double mid = -90 + 90 * i;   // up, right, down, left (screen y points down)
            var wedge = new Path { Data = Wedge(mid - 45 + GapDegrees, mid + 45 - GapDegrees), StrokeThickness = 1 };
            wedge.SetResourceReference(Shape.StrokeProperty, "B.Hair");
            _wedges[i] = wedge;
            _ring.Children.Add(wedge);

            var (label, icon) = i < slots.Count ? slots[i] : ("", "");
            var iv = new IconView { Data = icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center };
            var tb = new TextBlock { Text = label, FontSize = 12, TextAlignment = TextAlignment.Center, MaxWidth = 96, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
            _icons[i] = iv;
            _texts[i] = tb;
            var stack = new StackPanel { Width = 96, IsHitTestVisible = false };
            stack.Children.Add(iv);
            stack.Children.Add(tb);
            double r = (Inner + Outer) / 2, a = mid * Math.PI / 180;
            stack.Measure(new Size(96, double.PositiveInfinity));
            SetLeft(stack, centre.X + r * Math.Cos(a) - 48);
            SetTop(stack, centre.Y + r * Math.Sin(a) - stack.DesiredSize.Height / 2);
            _ring.Children.Add(stack);
        }
        var hub = new Ellipse { Width = (Inner - 8) * 2, Height = (Inner - 8) * 2, StrokeThickness = 1 };
        hub.SetResourceReference(Shape.FillProperty, "B.MenuBg");
        hub.SetResourceReference(Shape.StrokeProperty, "B.Hair");
        SetLeft(hub, centre.X - hub.Width / 2);
        SetTop(hub, centre.Y - hub.Height / 2);
        _ring.Children.Add(hub);

        Selected = -1;
        _ray.X1 = _ray.X2 = centre.X;
        _ray.Y1 = _ray.Y2 = centre.Y;
        Paint();
        Visibility = Visibility.Visible;
    }

    /// <summary>Follows the mouse: picks the wedge in its direction once it leaves the small dead zone.</summary>
    public void Track(Point p)
    {
        var d = p - _centre;
        _ray.X2 = p.X;
        _ray.Y2 = p.Y;
        int sel = -1;
        if (d.Length >= DeadZone)
        {
            double deg = Math.Atan2(d.Y, d.X) * 180 / Math.PI;      // -180..180, 0 = right, 90 = down
            sel = (int)Math.Floor(((deg + 90 + 45) % 360 + 360) % 360 / 90);   // 0 up, 1 right, 2 down, 3 left
        }
        if (sel != Selected) { Selected = sel; Paint(); }
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _ring.Children.Clear();
        Selected = -1;
    }

    private void Paint()
    {
        for (int i = 0; i < 4; i++)
        {
            if (_wedges[i] == null) continue;
            bool on = i == Selected;
            _wedges[i].SetResourceReference(Shape.FillProperty, on ? "B.Accent" : "B.MenuBg");
            if (on) { _texts[i].Foreground = Brushes.White; _icons[i].IconBrush = Brushes.White; }
            else
            {
                _texts[i].SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
                _icons[i].SetResourceReference(IconView.IconBrushProperty, "B.Text");
            }
        }
        _ray.Visibility = Selected >= 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private Geometry Wedge(double a0, double a1)
    {
        Point P(double r, double deg) => new(_centre.X + r * Math.Cos(deg * Math.PI / 180), _centre.Y + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(Outer, a0), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment(P(Outer, a1), new Size(Outer, Outer), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(Inner, a1), true));
        fig.Segments.Add(new ArcSegment(P(Inner, a0), new Size(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }
}
