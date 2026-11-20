using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Rodspaette.Core;

namespace Rodspaette.App;

/// <summary>Renders PDF pages for the printer, fitting each page to the paper (rotating landscape pages when needed).</summary>
public sealed class PdfPaginator : DocumentPaginator
{
    private readonly PdfDocument _doc;
    private readonly List<int> _pages;
    private readonly bool _annotations, _grayscale;
    private Size _paper;

    public PdfPaginator(PdfDocument doc, List<int> pages, Size paper, bool annotations, bool grayscale)
    {
        _doc = doc; _pages = pages; _paper = paper; _annotations = annotations; _grayscale = grayscale;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages.Count;
    public override Size PageSize { get => _paper; set => _paper = value; }
    public override IDocumentPaginatorSource Source => null!;

    public override DocumentPage GetPage(int pageNumber)
    {
        int p = _pages[pageNumber];
        var s = _doc.GetPageSize(p);
        double w0 = s.Width * 96 / 72, h0 = s.Height * 96 / 72;
        bool rotate = (w0 > h0) != (_paper.Width > _paper.Height);
        double dispW = rotate ? h0 : w0, dispH = rotate ? w0 : h0;
        double f = Math.Min(_paper.Width / dispW, _paper.Height / dispH);
        double ox = (_paper.Width - dispW * f) / 2, oy = (_paper.Height - dispH * f) / 2;

        // ~300 dpi, capped to keep memory sane on huge pages
        double pxScale = 300.0 / 72.0 * f;
        double maxSide = Math.Max(s.Width, s.Height) * pxScale;
        if (maxSide > 6000) pxScale *= 6000 / maxSide;
        int pw = Math.Max(1, (int)(s.Width * pxScale)), ph = Math.Max(1, (int)(s.Height * pxScale));
        var px = PdfWorker.Shared.Run(() => _doc.Render(p, pxScale, 0, 0, pw, ph, _annotations, printing: true, grayscale: _grayscale)).GetAwaiter().GetResult();
        var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, px, pw * 4);
        bmp.Freeze();

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (rotate)
            {
                dc.PushTransform(new TranslateTransform(ox + dispW * f, oy));
                dc.PushTransform(new RotateTransform(90));
                dc.DrawImage(bmp, new Rect(0, 0, w0 * f, h0 * f));
                dc.Pop();
                dc.Pop();
            }
            else dc.DrawImage(bmp, new Rect(ox, oy, w0 * f, h0 * f));
        }
        var box = new Rect(_paper);
        return new DocumentPage(dv, _paper, box, box);
    }
}

/// <summary>The simple print sheet: printer, copies, pages, colour, annotations, two-sided.</summary>
public sealed class PrintSheet : Window
{
    private readonly PdfDocument _doc;
    private readonly int _currentPage;
    private readonly string _title;
    private readonly ComboBox _printer = new() { Height = 32, FontSize = 13, MinWidth = 220 };
    private readonly TextBox _copies;
    private readonly TextBox _range;
    private readonly RadioButton _all, _current, _custom, _color, _bw;
    private readonly ToggleButton _annots, _duplex;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _pageLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private int _previewPage;

    public PrintSheet(Window owner, PdfDocument doc, int currentPage, string title)
    {
        _doc = doc; _currentPage = currentPage; _title = title; _previewPage = currentPage;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Foreground = Theme.GetBrush("Text");

        _copies = Field("1", 64);
        _range = Field("", 120);
        _range.GotKeyboardFocus += (_, _) => _custom!.IsChecked = true;
        _all = Seg("All", "pages", true);
        _current = Seg("Current", "pages", false);
        _custom = Seg("Range", "pages", false);
        _color = Seg("Colour", "color", true);
        _bw = Seg("Black & white", "color", false);
        _annots = new ToggleButton { Style = (Style)Application.Current.Resources["Switch"], IsChecked = true };
        _duplex = new ToggleButton { Style = (Style)Application.Current.Resources["Switch"], IsChecked = false };

        // ---------------- preview column
        var previewFrame = new Border
        {
            Width = 210, Height = 270, Background = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.2 },
            Child = _preview,
        };
        var prev = SmallButton(Icons.ChevronRight, flip: true);
        var next = SmallButton(Icons.ChevronRight, flip: false);
        prev.Click += (_, _) => { if (_previewPage > 0) { _previewPage--; LoadPreview(); } };
        next.Click += (_, _) => { if (_previewPage < _doc.PageCount - 1) { _previewPage++; LoadPreview(); } };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        pager.Children.Add(prev); pager.Children.Add(_pageLabel); pager.Children.Add(next);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(previewFrame);
        left.Children.Add(pager);
        var leftHost = new Border { Width = 290, Padding = new Thickness(28), Child = left };
        leftHost.SetResourceReference(Border.BackgroundProperty, "B.Viewer");

        // ---------------- form column
        var form = new StackPanel { Width = 400, Margin = new Thickness(24, 22, 24, 20) };
        form.Children.Add(new TextBlock { Text = "Print", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        form.Children.Add(Row("Printer", _printer));
        form.Children.Add(Row("Copies", _copies));
        var pagesRow = new StackPanel { Orientation = Orientation.Horizontal };
        pagesRow.Children.Add(Segmented(_all, _current, _custom));
        _range.Margin = new Thickness(8, 0, 0, 0);
        pagesRow.Children.Add(_range);
        form.Children.Add(Row("Pages", pagesRow));
        form.Children.Add(Row("Colour", Segmented(_color, _bw)));
        form.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6, 0, 10), Background = Theme.GetBrush("Hair") });
        form.Children.Add(SwitchRow("Include annotations", _annots));
        form.Children.Add(SwitchRow("Print on both sides", _duplex));

        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var more = new Button { Content = "More settings…", Style = (Style)Application.Current.Resources["TextButton"], Foreground = Theme.GetBrush("Accent"), Padding = new Thickness(0) };
        more.Click += (_, _) => PrintWithSystemDialog();
        DockPanel.SetDock(more, Dock.Left);
        buttons.Children.Add(more);
        var print = new Button { Content = "Print", Style = (Style)Application.Current.Resources["AccentButton"], MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.Resources["PlainButton"], MinWidth = 84 };
        print.Click += (_, _) => DoPrint();
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(print, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(print);
        buttons.Children.Add(cancel);
        form.Children.Add(buttons);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(leftHost);
        Grid.SetColumn(form, 1);
        grid.Children.Add(form);

        Content = new Border
        {
            Background = Theme.GetBrush("MenuBg"),
            BorderBrush = Theme.GetBrush("Hair"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(20),
            ClipToBounds = true,
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 10, Direction = 270, Opacity = 0.3 },
            Child = new Border { CornerRadius = new CornerRadius(16), ClipToBounds = true, Child = grid },
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key == Key.Enter && !_range.IsKeyboardFocused) DoPrint(); };
        MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };
        Loaded += (_, _) => { LoadPrinters(); LoadPreview(); };
    }

    // ------------------------------------------------------------- small UI helpers
    private static TextBox Field(string text, double width) =>
        new() { Text = text, Width = width, Style = (Style)Application.Current.Resources["Field"] };

    private static RadioButton Seg(string text, string group, bool on) =>
        new() { Content = text, GroupName = group, IsChecked = on, Style = (Style)Application.Current.Resources["SegButton"] };

    private static Border Segmented(params RadioButton[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) sp.Children.Add(i);
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = sp, HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(Border.BackgroundProperty, "B.Seg");
        return b;
    }

    private static Grid Row(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.GetBrush("Text2") };
        g.Children.Add(t);
        if (control is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static DockPanel SwitchRow(string label, ToggleButton sw)
    {
        var d = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        DockPanel.SetDock(sw, Dock.Right);
        d.Children.Add(sw);
        d.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        return d;
    }

    private static Button SmallButton(string icon, bool flip)
    {
        var iv = IconView.Make(icon, 13);
        if (flip) { iv.RenderTransformOrigin = new Point(0.5, 0.5); iv.RenderTransform = new ScaleTransform(-1, 1); }
        return new Button { Content = iv, Style = (Style)Application.Current.Resources["IconButton"], Width = 28, Height = 28, Background = Theme.GetBrush("Hover") };
    }

    // ------------------------------------------------------------- data
    private void LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var def = LocalPrintServer.GetDefaultPrintQueue();
            var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
            foreach (var q in queues.OrderBy(q => q.FullName)) _printer.Items.Add(q.FullName);
            _printer.SelectedItem = def?.FullName;
            if (_printer.SelectedIndex < 0 && _printer.Items.Count > 0) _printer.SelectedIndex = 0;
        }
        catch { }
    }

    private async void LoadPreview()
    {
        _pageLabel.Text = $"Page {_previewPage + 1} of {_doc.PageCount}";
        var s = _doc.GetPageSize(_previewPage);
        double scale = Math.Min(420 / s.Width, 540 / s.Height);
        int w = Math.Max(1, (int)(s.Width * scale)), h = Math.Max(1, (int)(s.Height * scale));
        int page = _previewPage;
        bool annots = _annots.IsChecked == true;
        try
        {
            var px = await PdfWorker.Shared.Run(() => _doc.Render(page, scale, 0, 0, w, h, annots, printing: true));
            if (page != _previewPage) return;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            _preview.Source = bmp;
        }
        catch { }
    }

    private List<int>? SelectedPages()
    {
        int n = _doc.PageCount;
        if (_all.IsChecked == true) return Enumerable.Range(0, n).ToList();
        if (_current.IsChecked == true) return new List<int> { _currentPage };
        var list = new List<int>();
        foreach (var part in _range.Text.Split(',', ';'))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;
            var m = Regex.Match(t, @"^(\d+)\s*[-–]\s*(\d+)$");
            if (m.Success)
            {
                int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
                if (a > b) (a, b) = (b, a);
                for (int i = a; i <= b; i++) if (i >= 1 && i <= n) list.Add(i - 1);
            }
            else if (int.TryParse(t, out int single) && single >= 1 && single <= n) list.Add(single - 1);
            else return null;
        }
        return list.Count > 0 ? list : null;
    }

    private void DoPrint()
    {
        var pages = SelectedPages();
        if (pages == null) { Dialogs.Info(this, "Check the page range", $"Use page numbers between 1 and {_doc.PageCount}, like 1-3, 7."); return; }
        if (!int.TryParse(_copies.Text.Trim(), out int copies) || copies < 1 || copies > 999) copies = 1;
        try
        {
            using var server = new LocalPrintServer();
            var name = _printer.SelectedItem as string;
            PrintQueue queue = name != null
                ? server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }).First(q => q.FullName == name)
                : LocalPrintServer.GetDefaultPrintQueue();
            var ticket = queue.DefaultPrintTicket.Clone();
            ticket.CopyCount = copies;
            ticket.OutputColor = _bw.IsChecked == true ? OutputColor.Monochrome : OutputColor.Color;
            ticket.Duplexing = _duplex.IsChecked == true ? Duplexing.TwoSidedLongEdge : Duplexing.OneSided;
            var paper = PaperSize(ticket);
            var paginator = new PdfPaginator(_doc, pages, paper, _annots.IsChecked == true, _bw.IsChecked == true);
            Mouse.OverrideCursor = Cursors.Wait;
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(paginator, ticket);
            Close();
        }
        catch (Exception ex)
        {
            Dialogs.Info(this, "Printing failed", ex.Message);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private static Size PaperSize(PrintTicket ticket)
    {
        var m = ticket.PageMediaSize;
        if (m?.Width is double w && m.Height is double h && w > 0 && h > 0)
        {
            if (ticket.PageOrientation == PageOrientation.Landscape) return new Size(Math.Max(w, h), Math.Min(w, h));
            return new Size(w, h);
        }
        return new Size(793.7, 1122.5); // A4
    }

    private void PrintWithSystemDialog()
    {
        var dlg = new PrintDialog { UserPageRangeEnabled = true, MinPage = 1, MaxPage = (uint)_doc.PageCount };
        if (dlg.ShowDialog() != true) return;
        var pages = dlg.PageRangeSelection == PageRangeSelection.UserPages
            ? Enumerable.Range(dlg.PageRange.PageFrom - 1, Math.Max(1, dlg.PageRange.PageTo - dlg.PageRange.PageFrom + 1)).Where(i => i >= 0 && i < _doc.PageCount).ToList()
            : Enumerable.Range(0, _doc.PageCount).ToList();
        var paper = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        bool mono = dlg.PrintTicket?.OutputColor == OutputColor.Monochrome;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            dlg.PrintDocument(new PdfPaginator(_doc, pages, paper, _annots.IsChecked == true, mono), _title);
            Close();
        }
        catch (Exception ex) { Dialogs.Info(this, "Printing failed", ex.Message); }
        finally { Mouse.OverrideCursor = null; }
    }
}
