using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Rodspaette.App;

/// <summary>Settings → Function dial…: the four commands of the right-drag function dial.</summary>
public static class SettingsDialog
{
    public static void Show(MainWindow owner)
    {
        var w = Dialogs.MakeWindow(owner, 520);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Function dial", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Text"), Margin = new Thickness(0, 0, 0, 14) });

        var cross = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 3; i++)
        {
            cross.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cross.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var hub = new Grid { Width = 64, Height = 64, Margin = new Thickness(10) };
        hub.Children.Add(new Ellipse { Fill = Theme.GetBrush("Card"), Stroke = Theme.GetBrush("Hair"), StrokeThickness = 1 });
        hub.Children.Add(new IconView { Data = Icons.Mouse, Width = 24, Height = 24, IconBrush = Theme.GetBrush("Muted") });
        Grid.SetRow(hub, 1); Grid.SetColumn(hub, 1);
        cross.Children.Add(hub);

        var slotButtons = new Button[4];
        (int row, int col)[] cells = { (0, 1), (1, 2), (2, 1), (1, 0) };   // up, right, down, left
        void Refresh()
        {
            var all = owner.MouseActions().ToDictionary(a => a.Id);
            var slots = owner.MouseMenuSlots();
            for (int i = 0; i < 4; i++)
            {
                var a = all[slots[i]];
                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new IconView { Data = a.Icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center });
                content.Children.Add(new TextBlock { Text = a.Label, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 118 });
                slotButtons[i].Content = content;
            }
        }
        for (int i = 0; i < 4; i++)
        {
            int slot = i;
            var b = new Button
            {
                Width = 132, Height = 60,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(1),
                ToolTip = new[] { "Up", "Right", "Down", "Left" }[i],
            };
            b.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
            b.SetResourceReference(Control.BackgroundProperty, "B.Card");
            b.SetResourceReference(Control.BorderBrushProperty, "B.Hair");
            b.Click += (_, _) =>
            {
                var current = owner.MouseMenuSlots()[slot];
                var menu = new PopupMenu();
                foreach (var a in owner.MouseActions())
                {
                    var id = a.Id;
                    menu.Item(a.Label, null, () => { owner.SetMouseMenuSlot(slot, id); Refresh(); }, a.Icon,
                              id == current ? Theme.GetBrush("Accent") : null);
                }
                menu.Open(b);
            };
            slotButtons[i] = b;
            Grid.SetRow(b, cells[i].row); Grid.SetColumn(b, cells[i].col);
            cross.Children.Add(b);
        }
        Refresh();
        stack.Children.Add(cross);

        // ---- buttons
        var row = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        var reset = new Button { Content = "Reset", MinWidth = 84 };
        reset.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        reset.Click += (_, _) => { owner.ResetMouseMenu(); Refresh(); };
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
        w.ShowDialog();
    }
}
