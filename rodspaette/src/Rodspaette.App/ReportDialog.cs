using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rodspaette.App;

/// <summary>Appends handled and unhandled errors to %APPDATA%\Rødspætte PDF\errors.log.</summary>
public static class ErrorLog
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "errors.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(FilePath, $"{DateTime.Now:u} [{context}] {ex}\n\n");
        }
        catch { }
    }

    /// <summary>The newest entries, at most <paramref name="maxChars"/> characters.</summary>
    public static string Tail(int maxChars = 6000)
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            var text = File.ReadAllText(FilePath);
            return text.Length <= maxChars ? text : "…\n" + text[^maxChars..];
        }
        catch { return ""; }
    }
}

/// <summary>
/// "Update request…": text and pasted pictures, saved with app context into
/// Documents\Rødspætte PDF reports\&lt;date time&gt;\ so Claude can read them when asked.
/// </summary>
public static class ReportDialog
{
    public static string ReportsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rødspætte PDF reports");

    /// <summary>Shows the dialog. Returns the folder the report was saved to, or null if cancelled.</summary>
    public static string? Show(Window owner, string context)
    {
        var w = Dialogs.MakeWindow(owner, 560);
        string? saved = null;
        var pictures = new List<BitmapSource>();

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Update request", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.GetBrush("Text") });

        var text = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 170,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
        };
        text.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        stack.Children.Add(text);

        var thumbs = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(thumbs);

        void AddPicture(BitmapSource bmp)
        {
            if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
            pictures.Add(bmp);
            var cell = new Grid { Width = 96, Height = 72, Margin = new Thickness(0, 8, 8, 0) };
            cell.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.GetBrush("Hair"),
                Background = Theme.GetBrush("Card"),
                ClipToBounds = true,
                Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill },
            });
            var remove = new Button { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 3, 0), ToolTip = "Remove", Content = new IconView { Data = Icons.Close, Width = 11, Height = 11, StrokeWidth = 2.4 } };
            remove.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            remove.SetResourceReference(Control.BackgroundProperty, "B.MenuBg");
            remove.Click += (_, _) => { pictures.Remove(bmp); thumbs.Children.Remove(cell); };
            cell.Children.Add(remove);
            thumbs.Children.Add(cell);
        }

        // Ctrl+V: a picture on the clipboard (or copied picture files) becomes an attachment; text pastes as usual
        bool TryPastePictures()
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img) { AddPicture(img); return true; }
                if (Clipboard.ContainsFileDropList()) return AddFiles(Clipboard.GetFileDropList().Cast<string>());
            }
            catch { }
            return false;
        }
        bool AddFiles(IEnumerable<string> files)
        {
            bool any = false;
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff")) continue;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(f); bi.EndInit();
                    AddPicture(bi); any = true;
                }
                catch { }
            }
            return any;
        }
        text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && TryPastePictures()) e.Handled = true;
        };
        w.AllowDrop = true;
        w.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        w.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && AddFiles(files)) e.Handled = true;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "PlainButton");
        var send = new Button { Content = "Send", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        send.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        row.Children.Add(cancel);
        row.Children.Add(send);
        stack.Children.Add(row);

        cancel.Click += (_, _) => w.Close();
        send.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(text.Text) && pictures.Count == 0) { text.Focus(); return; }
            try { saved = Save(text.Text, pictures, context); w.Close(); }
            catch (Exception ex) { ErrorLog.Write("report", ex); Dialogs.Info(w, "Could not save the request", ex.Message); }
        };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };

        w.Content = Dialogs.Card(stack);
        w.Loaded += (_, _) => text.Focus();
        w.ShowDialog();
        return saved;
    }

    private static string Save(string userText, List<BitmapSource> pictures, string context)
    {
        var stamp = DateTime.Now;
        var dir = Path.Combine(ReportsFolder, stamp.ToString("yyyy-MM-dd HH.mm.ss"));
        Directory.CreateDirectory(dir);

        var md = new StringBuilder();
        md.AppendLine("# Rødspætte PDF update request");
        md.AppendLine();
        md.AppendLine("> Note for Claude: this request was filed from inside the app. Don't act on it, change code, or reply about it");
        md.AppendLine("> unless the user asks about it in the Claude app. Just keep it on record until then.");
        md.AppendLine();
        md.AppendLine($"Filed: {stamp:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine();
        md.AppendLine("## Request");
        md.AppendLine();
        md.AppendLine(string.IsNullOrWhiteSpace(userText) ? "(no text)" : userText.Trim());
        md.AppendLine();

        if (pictures.Count > 0)
        {
            md.AppendLine("## Pictures");
            md.AppendLine();
            for (int i = 0; i < pictures.Count; i++)
            {
                var name = $"picture-{i + 1}.png";
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(pictures[i]));
                using (var fs = File.Create(Path.Combine(dir, name))) enc.Save(fs);
                md.AppendLine($"![{name}]({name})");
            }
            md.AppendLine();
        }

        md.AppendLine("## App state");
        md.AppendLine();
        md.AppendLine(context.TrimEnd());
        md.AppendLine();

        var log = ErrorLog.Tail();
        md.AppendLine("## Recent error log");
        md.AppendLine();
        md.AppendLine("```");
        md.AppendLine(string.IsNullOrWhiteSpace(log) ? "(empty)" : log.TrimEnd());
        md.AppendLine("```");

        File.WriteAllText(Path.Combine(dir, "report.md"), md.ToString(), new UTF8Encoding(false));
        return dir;
    }
}
