using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace Rodspaette.App;

/// <summary>Small themed dialogs (message, choice, text input) in the app's style.</summary>
public static class Dialogs
{
    internal static Window MakeWindow(Window? owner, double width)
    {
        var w = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = width,
            ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            Topmost = owner == null,
        };
        return w;
    }

    internal static Border Card(UIElement content) => new()
    {
        Background = Theme.GetBrush("MenuBg"),
        BorderBrush = Theme.GetBrush("Hair"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(22, 20, 22, 18),
        Margin = new Thickness(18),
        Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.28 },
        Child = content,
    };

    /// <summary>Shows a message with buttons. Returns the index of the clicked button, or -1 on Escape.</summary>
    public static int Ask(Window? owner, string title, string message, params string[] buttons)
    {
        var w = MakeWindow(owner, 440);
        int result = -1;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Text"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(message))
            stack.Children.Add(new TextBlock { Text = message, FontSize = 13, Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.GetBrush("Text2"), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int idx = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)Application.Current.Resources[i == 0 ? "AccentButton" : "PlainButton"],
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 84,
            };
            b.Click += (_, _) => { result = idx; w.Close(); };
            row.Children.Add(b);
        }
        stack.Children.Add(row);
        w.Content = Card(stack);
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = -1; w.Close(); }
            else if (e.Key == Key.Enter) { result = 0; w.Close(); }
        };
        w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.ShowDialog();
        return result;
    }

    public static void Info(Window? owner, string title, string message) => Ask(owner, title, message, "OK");

    /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
    public static string? Input(Window? owner, string title, string initial, string okText = "OK", bool password = false)
    {
        var w = MakeWindow(owner, 420);
        string? result = null;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Text"), Margin = new Thickness(0, 0, 0, 12) });
        Control input;
        Func<string> read;
        if (password)
        {
            var pb = new PasswordBox
            {
                Height = 32, FontSize = 13, Padding = new Thickness(8, 6, 8, 0),
                Background = Theme.GetBrush("Field"), Foreground = Theme.GetBrush("Text"), BorderBrush = Theme.GetBrush("FieldBorder"),
            };
            input = pb; read = () => pb.Password;
        }
        else
        {
            var tb = new TextBox { Text = initial, Style = (Style)Application.Current.Resources["Field"] };
            tb.SelectAll();
            input = tb; read = () => tb.Text;
        }
        stack.Children.Add(input);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.Resources["PlainButton"], MinWidth = 84 };
        var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources["AccentButton"], MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => w.Close();
        ok.Click += (_, _) => { result = read(); w.Close(); };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        stack.Children.Add(row);
        w.Content = Card(stack);
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) w.Close();
            else if (e.Key == Key.Enter) { result = read(); w.Close(); }
        };
        w.Loaded += (_, _) => input.Focus();
        w.ShowDialog();
        return result;
    }
}
