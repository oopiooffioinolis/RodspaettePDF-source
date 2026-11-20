using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rodspaette.App;

/// <summary>Settings → Shortcuts…: every command and its key. Click a key, then press the new one.</summary>
public static class ShortcutsDialog
{
    public static void Show(Window owner)
    {
        var w = Dialogs.MakeWindow(owner, 560);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Shortcuts", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Text") });
        stack.Children.Add(new TextBlock
        {
            Text = "Click a shortcut, then press the keys you want. Backspace removes it, Esc stops.",
            FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Theme.GetBrush("Text2"),
        });

        string? recording = null;
        var keyText = new Dictionary<string, TextBlock>();
        var keyBox = new Dictionary<string, Border>();
        var resetButtons = new Dictionary<string, Button>();
        var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Theme.GetBrush("Text2"), Visibility = Visibility.Collapsed };
        void Note(string? text)
        {
            note.Text = text ?? "";
            note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void Refresh()
        {
            foreach (var c in Shortcuts.All)
            {
                var k = Shortcuts.Get(c.Id);
                bool rec = recording == c.Id;
                var t = keyText[c.Id];
                t.Text = rec ? "Press keys…" : k.IsNone ? "None" : k.ToString();
                t.Foreground = Theme.GetBrush(rec ? "Accent" : k.IsNone ? "Muted" : "Text");
                keyBox[c.Id].BorderBrush = Theme.GetBrush(rec ? "Accent" : "FieldBorder");
                resetButtons[c.Id].Visibility = Shortcuts.IsDefault(c.Id) ? Visibility.Hidden : Visibility.Visible;
            }
        }

        var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        string? group = null;
        foreach (var cmd in Shortcuts.All)
        {
            var c = cmd;
            if (c.Group != group)
            {
                group = c.Group;
                list.Children.Add(new TextBlock
                {
                    Text = group, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Muted"),
                    Margin = new Thickness(0, list.Children.Count == 0 ? 2 : 14, 0, 4),
                });
            }
            var row = new DockPanel { Height = 34, LastChildFill = true };

            var reset = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "Back to " + (c.Default.IsNone ? "none" : c.Default.ToString()),
                Content = new IconView { Data = Icons.Undo, Width = 14, Height = 14 },
            };
            reset.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            reset.Click += (_, _) =>
            {
                recording = null;
                var def = c.Default;
                var taken = Shortcuts.Set(c.Id, def);
                Note(taken != null ? $"{def} was taken from “{taken.Label}”, which now has none." : null);
                Refresh();
            };
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(reset);

            var text = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Border
            {
                MinWidth = 128, Height = 28, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
                Background = Theme.GetBrush("Field"), Child = text, ToolTip = "Click, then press the new keys",
            };
            box.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                recording = recording == c.Id ? null : c.Id;
                Note(null);
                Refresh();
            };
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);

            row.Children.Add(new TextBlock
            {
                Text = c.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.GetBrush("Text"),
            });
            keyText[c.Id] = text;
            keyBox[c.Id] = box;
            resetButtons[c.Id] = reset;
            list.Children.Add(row);
        }
        stack.Children.Add(new ScrollViewer
        {
            Content = list,
            Margin = new Thickness(0, 10, 0, 0),
            MaxHeight = Math.Max(240, Math.Min(560, SystemParameters.WorkArea.Height - 260)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        stack.Children.Add(note);

        // ---- buttons
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var resetAll = new Button { Content = "Reset all", MinWidth = 84 };
        resetAll.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        resetAll.Click += (_, _) => { recording = null; Shortcuts.ResetAll(); Note(null); Refresh(); };
        var done = new Button { Content = "Done", MinWidth = 84 };
        done.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        done.Click += (_, _) => w.Close();
        DockPanel.SetDock(done, Dock.Right);
        buttons.Children.Add(done);
        buttons.Children.Add(resetAll);
        stack.Children.Add(buttons);
        Refresh();

        w.Content = Dialogs.Card(stack);
        w.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (recording == null)
            {
                if (key is Key.Escape or Key.Enter) { e.Handled = true; w.Close(); }
                return;
            }
            e.Handled = true;
            if (KeyChord.IsModifier(key)) return;   // wait for the key that goes with Ctrl, Alt or Shift
            var id = recording;
            if (key == Key.Escape) { recording = null; Refresh(); return; }
            if (key == Key.Back) { Shortcuts.Set(id, default); recording = null; Note(null); Refresh(); return; }
            var mods = Keyboard.Modifiers;
            bool bare = (mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0;
            if (bare && key is Key.Enter or Key.Space or Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down
                    or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                Note("That key moves around the page. Add Ctrl or Alt to it.");
                return;
            }
            var chord = new KeyChord(mods, KeyChord.Normalize(key));
            var taken = Shortcuts.Set(id, chord);
            recording = null;
            Note(taken != null && taken.Id != id ? $"{chord} was taken from “{taken.Label}”, which now has none." : null);
            Refresh();
        };
        w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.ShowDialog();
    }
}
