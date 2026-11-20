using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Rodspaette.App;

/// <summary>
/// Right-click menu built from the same card and row styles as the title-bar menu,
/// so every dropdown in the app looks alike. Rows close the menu before running their action.
/// The menu grows as tall as the screen allows, so it only scrolls when the screen itself is too short.
/// </summary>
public sealed class PopupMenu
{
    public static PopupMenu? Current { get; private set; }

    private readonly Popup _popup;
    private readonly StackPanel _rows = new() { MinWidth = 240 };
    private readonly ScrollViewer _scroll;
    private readonly HashSet<string> _marks = new();

    public PopupMenu()
    {
        _scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var card = new Border { Child = _scroll };
        card.SetResourceReference(FrameworkElement.StyleProperty, "MenuCard");
        _popup = new Popup
        {
            Child = card,
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.MousePoint,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -8,
            VerticalOffset = -8,
        };
        _popup.Closed += (_, _) => { if (Current == this) Current = null; };
    }

    public int Count => _rows.Children.Count;

    /// <summary>Notes that a kind of row is already in the menu, so a later section can leave out its own copy.</summary>
    public void Mark(string key) => _marks.Add(key);

    public bool Has(string key) => _marks.Contains(key);

    /// <summary>A text row with an optional line icon, colour dot and shortcut on the right.</summary>
    public void Item(string text, string? shortcut, Action action, string? icon = null, Brush? dot = null, bool enabled = true)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (shortcut != null)
        {
            var g = new TextBlock { Text = shortcut, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.ForegroundProperty, "B.Muted");
            DockPanel.SetDock(g, Dock.Right);
            dock.Children.Add(g);
        }
        if (dot != null)
        {
            var e = new Ellipse { Width = 9, Height = 9, Fill = dot, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(e, Dock.Right);
            dock.Children.Add(e);
        }
        if (icon != null)
        {
            var iv = new IconView { Data = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), StrokeWidth = 2 };
            DockPanel.SetDock(iv, Dock.Left);
            dock.Children.Add(iv);
        }
        dock.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var b = new Button { Content = dock, IsEnabled = enabled };
        b.SetResourceReference(FrameworkElement.StyleProperty, "MenuItemButton");
        b.Click += (_, _) => { Close(); action(); };
        _rows.Children.Add(b);
    }

    public void Separator()
    {
        if (_rows.Children.Count == 0 || _rows.Children[^1] is Border { Tag: "sep" }) return;
        var line = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5), Tag = "sep" };
        line.SetResourceReference(Border.BackgroundProperty, "B.Hair");
        _rows.Children.Add(line);
    }

    /// <summary>A row of icon buttons; the one at <paramref name="selected"/> is shown as active.</summary>
    public void IconRow(IReadOnlyList<(string icon, string tip, Action action)> buttons, int selected)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        for (int i = 0; i < buttons.Count; i++)
        {
            var (icon, tip, action) = buttons[i];
            var b = new Button { Width = 34, Height = 34, ToolTip = tip, Content = new IconView { Data = icon, Width = 17, Height = 17 } };
            b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            if (i == selected)
            {
                b.SetResourceReference(Control.BackgroundProperty, "B.AccentSoft");
                b.SetResourceReference(Control.ForegroundProperty, "B.Accent");
            }
            b.Click += (_, _) => { Close(); action(); };
            row.Children.Add(b);
        }
        _rows.Children.Add(row);
    }

    /// <summary>A row of colour swatches, with a small icon at the left naming what the colour is for.</summary>
    public void SwatchRow(string forIcon, string forTip, IReadOnlyList<string> colors, string current, Action<string> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 2) };
        var forView = new IconView { Data = forIcon, Width = 15, Height = 15 };
        forView.SetResourceReference(IconView.IconBrushProperty, "B.Muted");
        var label = new Border { Width = 34, Height = 30, ToolTip = forTip, Background = Brushes.Transparent, Child = forView };
        row.Children.Add(label);
        foreach (var hex in colors)
        {
            var fill = (Brush)new BrushConverter().ConvertFromString(hex)!;
            bool on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 20, Height = 20 };
            dot.Children.Add(new Ellipse { Fill = fill, Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), StrokeThickness = 1 });
            if (on)
            {
                var ring = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2, Margin = new Thickness(-3) };
                ring.SetResourceReference(Shape.StrokeProperty, "B.Accent");
                dot.Children.Add(ring);
            }
            var b = new Button { Width = 30, Height = 30, ToolTip = hex, Content = dot };
            b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            b.Click += (_, _) => { Close(); pick(hex); };
            row.Children.Add(b);
        }
        _rows.Children.Add(row);
    }

    public void Open(UIElement target)
    {
        if (_rows.Children.Count > 0 && _rows.Children[^1] is Border { Tag: "sep" }) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        if (_rows.Children.Count == 0) return;
        Current?.Close();
        Current = this;
        _scroll.MaxHeight = ScreenRoom(target);
        _popup.PlacementTarget = target;
        _popup.IsOpen = true;
    }

    public void Close() => _popup.IsOpen = false;

    /// <summary>
    /// The height of the screen under the mouse (without the taskbar) in WPF units, less the card's margin,
    /// border and padding. Windows moves a menu up when it would run off the bottom, so a menu this tall still fits.
    /// </summary>
    private static double ScreenRoom(UIElement target)
    {
        double h = SystemParameters.WorkArea.Height;
        try
        {
            if (Win32.GetCursorPos(out var pt))
            {
                var mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
                var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref info))
                {
                    double scale = PresentationSource.FromVisual(target)?.CompositionTarget?.TransformToDevice.M22 ?? 1;
                    if (scale <= 0) scale = 1;
                    h = (info.rcWork.Bottom - info.rcWork.Top) / scale;
                }
            }
        }
        catch { }
        return Math.Max(240, h - 48);
    }
}
