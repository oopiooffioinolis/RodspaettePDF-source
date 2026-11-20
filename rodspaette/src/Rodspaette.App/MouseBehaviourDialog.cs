using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Rodspaette.App;

/// <summary>
/// Settings → Mouse behaviour…: scroll sensitivity, how far the page moves for each turn of the mouse wheel (and
/// each touchpad swipe). 1× is Windows' usual distance; the slider runs from a quarter to four times that, with 1×
/// in the middle. A box underneath scrolls at the chosen speed so it can be tried straight away.
/// </summary>
public static class MouseBehaviourDialog
{
    private const double Min = 0.25, Max = 4;

    public static void Show(Window owner, AppSettings s)
    {
        var w = Dialogs.MakeWindow(owner, 460);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Mouse behaviour", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Text"), Margin = new Thickness(0, 0, 0, 16) });

        // ---- "Scroll sensitivity ........ 1×"
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var valueText = new TextBlock { FontSize = 13, Foreground = Theme.GetBrush("Text2") };
        DockPanel.SetDock(valueText, Dock.Right);
        head.Children.Add(valueText);
        head.Children.Add(new TextBlock { Text = "Scroll sensitivity", FontSize = 13, Foreground = Theme.GetBrush("Text") });
        stack.Children.Add(head);

        // ---- the slider: a rail, the part up to the value in the accent colour, and a round knob
        var track = new Grid { Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(9, 0, 9, 0), Focusable = true, FocusVisualStyle = null };
        var rail = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = Theme.GetBrush("Seg") };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Background = Theme.GetBrush("Accent") };
        var knob = new Ellipse
        {
            Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Fill = Theme.GetBrush("Card"), Stroke = Theme.GetBrush("Hair"), StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = 0.25 },
        };
        // the middle (1×) is marked with a small tick
        var tick = new Border { Width = 2, Height = 10, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Background = Theme.GetBrush("Edge") };
        track.Children.Add(rail);
        track.Children.Add(tick);
        track.Children.Add(fill);
        track.Children.Add(knob);
        stack.Children.Add(track);

        var ends = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var faster = new TextBlock { Text = "Faster", FontSize = 11.5, Foreground = Theme.GetBrush("Muted") };
        DockPanel.SetDock(faster, Dock.Right);
        ends.Children.Add(faster);
        ends.Children.Add(new TextBlock { Text = "Slower", FontSize = 11.5, Foreground = Theme.GetBrush("Muted") });
        stack.Children.Add(ends);

        static double ToT(double v) => Math.Log(v / Min) / Math.Log(Max / Min);
        static double FromT(double t)
        {
            double v = Min * Math.Pow(Max / Min, Math.Clamp(t, 0, 1));
            return Math.Abs(v - 1) < 0.06 ? 1 : Math.Round(v, 2);   // easy to land back on the usual speed
        }
        void ShowValue(double v)
        {
            double width = track.ActualWidth, t = ToT(v);
            fill.Width = Math.Max(0, t * width);
            knob.Margin = new Thickness(t * width - 9, 0, 0, 0);
            tick.Margin = new Thickness(width / 2 - 1, 0, 0, 0);
            valueText.Text = v.ToString("0.##", CultureInfo.InvariantCulture) + "×";
        }
        void Set(double v, bool save)
        {
            s.ScrollSpeed = Math.Clamp(v, Min, Max);
            ShowValue(s.ScrollSpeed);
            if (save) s.Save();
        }
        track.SizeChanged += (_, _) => ShowValue(s.ScrollSpeed);
        track.MouseLeftButtonDown += (_, e) =>
        {
            track.Focus();
            track.CaptureMouse();
            Set(FromT(e.GetPosition(track).X / Math.Max(1, track.ActualWidth)), false);
            e.Handled = true;
        };
        track.MouseMove += (_, e) => { if (track.IsMouseCaptured) Set(FromT(e.GetPosition(track).X / Math.Max(1, track.ActualWidth)), false); };
        track.MouseLeftButtonUp += (_, _) => { if (track.IsMouseCaptured) track.ReleaseMouseCapture(); };
        track.LostMouseCapture += (_, _) => s.Save();
        track.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Down) { Set(FromT(ToT(s.ScrollSpeed) - 0.05), true); e.Handled = true; }
            else if (e.Key is Key.Right or Key.Up) { Set(FromT(ToT(s.ScrollSpeed) + 0.05), true); e.Handled = true; }
        };

        // ---- try it: a box that scrolls at the chosen speed
        stack.Children.Add(new TextBlock { Text = "Try it here", FontSize = 12, Foreground = Theme.GetBrush("Muted"), Margin = new Thickness(0, 0, 0, 6) });
        var lines = new StackPanel { Margin = new Thickness(12, 6, 12, 6) };
        for (int i = 1; i <= 150; i++)
            lines.Children.Add(new TextBlock { Text = $"Line {i}", Height = 22, FontSize = 12.5, Foreground = Theme.GetBrush(i % 5 == 0 ? "Text" : "Text2") });
        var trial = new ScrollViewer { Content = lines, Height = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        trial.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            double px = -e.Delta * PdfViewer.WheelStep(trial.ViewportHeight) * s.ScrollSpeed;
            trial.ScrollToVerticalOffset(Math.Clamp(trial.VerticalOffset + px, 0, trial.ScrollableHeight));
        };
        var box = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(1),
            BorderBrush = Theme.GetBrush("Hair"), Background = Theme.GetBrush("Card"), Child = trial,
        };
        stack.Children.Add(box);

        // ---- buttons
        var row = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        var reset = new Button { Content = "Reset", MinWidth = 84 };
        reset.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        reset.Click += (_, _) => Set(1, true);
        var done = new Button { Content = "Done", MinWidth = 84 };
        done.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        done.Click += (_, _) => w.Close();
        DockPanel.SetDock(done, Dock.Right);
        row.Children.Add(done);
        row.Children.Add(reset);
        stack.Children.Add(row);

        w.Content = Dialogs.Card(stack);
        w.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) w.Close(); };
        w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.Loaded += (_, _) => track.Focus();
        w.ShowDialog();
        s.Save();
    }
}
