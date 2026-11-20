using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rodspaette.Core;

namespace Rodspaette.App;

/// <summary>
/// ☰ → Page numbers…: changes what the pages are called (the PDF's page labels), for example when page 14 of the
/// file is printed as 1. The change is a range of pages, the number one of them reads, and a style; the numbers
/// count on from there in both directions (so pages before it can fall below 1).
/// Every page has to end up with exactly one number that no other page has, so before anything is kept the dialog
/// asks, one run of pages at a time, how to number pages that would get a number below 1, no number, or the same
/// number as another page. Then it shows the new numbering to apply.
/// </summary>
public sealed class PageNumbersDialog
{
    private static readonly (PageLabelStyle Style, string Text)[] Styles =
    {
        (PageLabelStyle.Decimal, "1, 2, 3"), (PageLabelStyle.RomanLower, "i, ii, iii"), (PageLabelStyle.RomanUpper, "I, II, III"),
        (PageLabelStyle.LettersLower, "a, b, c"), (PageLabelStyle.LettersUpper, "A, B, C"),
    };

    private readonly Window _w;
    private readonly int _n;
    private readonly PageNumber[] _now;
    private readonly StackPanel _body = new();
    private readonly DockPanel _buttons = new() { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };

    // the edit (kept when going back to it)
    private readonly TextBox _from, _to, _refPage, _refNumber, _shift;
    private PageLabelStyle _style;
    private bool _syncing;

    // the numbering being worked out, and how it was before each answer (for Back)
    private PageNumber[] _pages = Array.Empty<PageNumber>();
    private bool[] _locked = Array.Empty<bool>();
    private readonly Stack<(PageNumber[] Pages, bool[] Locked)> _history = new();
    private List<PageLabelRange>? _result;

    /// <returns>The new numbering, or null when cancelled.</returns>
    public static List<PageLabelRange>? Show(Window owner, int pageCount, IReadOnlyList<PageLabelRange>? current, int currentPage)
    {
        var d = new PageNumbersDialog(owner, pageCount, current, currentPage);
        d._w.ShowDialog();
        return d._result;
    }

    private PageNumbersDialog(Window owner, int pageCount, IReadOnlyList<PageLabelRange>? current, int currentPage)
    {
        _n = pageCount;
        _now = PageNumbering.Expand(current, pageCount);
        _w = Dialogs.MakeWindow(owner, 500);
        _title.Foreground = Theme.GetBrush("Text");

        // defaults: the page in view, the number it reads now, and the run of pages it belongs to
        int p = Math.Clamp(currentPage, 0, pageCount - 1);
        var (s, e) = PageNumbering.SectionOf(_now, p);
        _style = _now[p].IsNumbered ? _now[p].Style : PageLabelStyle.Decimal;
        _from = NumberBox((s + 1).ToString(CultureInfo.InvariantCulture), 58);
        _to = NumberBox((e + 1).ToString(CultureInfo.InvariantCulture), 58);
        _refPage = NumberBox((p + 1).ToString(CultureInfo.InvariantCulture), 58);
        _refNumber = NumberBox((_now[p].IsNumbered ? _now[p].Number : p + 1).ToString(CultureInfo.InvariantCulture), 70);
        _shift = NumberBox("0", 70);
        _refPage.TextChanged += (_, _) => SyncShift();
        _refNumber.TextChanged += (_, _) => SyncShift();
        _shift.TextChanged += (_, _) => SyncReads();
        SyncShift();

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_body);
        stack.Children.Add(_buttons);
        _w.Content = Dialogs.Card(stack);
        _w.KeyDown += (_, ev) => { if (ev.Key == Key.Escape) _w.Close(); };
        _w.MouseLeftButtonDown += (_, ev) => { if (ev.ButtonState == MouseButtonState.Pressed) try { _w.DragMove(); } catch { } };
        BuildEdit();
        ShowEdit();
        _w.Loaded += (_, _) => { _refNumber.Focus(); _refNumber.SelectAll(); };
    }

    // ================================================================= step 1: the change
    private readonly StackPanel _edit = new();
    private TextBlock _editError = new();
    private TextBlock _editPreview = new();

    /// <summary>Built once, so going Back finds the fields as they were left.</summary>
    private void BuildEdit()
    {
        _edit.Children.Add(Row(Label("Pages", 92), _from, Label("to", 0, 10), _to, Muted($"of {_n}", 10)));
        _edit.Children.Add(Row(Label("Page", 92), _refPage, Label("reads", 0, 10), _refNumber, Label("Shift", 0, 22), _shift));

        var styles = new WrapPanel();
        foreach (var (style, text) in Styles)
        {
            var b = new RadioButton { Content = text, GroupName = "pn-style", IsChecked = style == _style, Margin = new Thickness(0, 0, 6, 6) };
            b.SetResourceReference(FrameworkElement.StyleProperty, "SegButton");
            var st = style;
            b.Checked += (_, _) => { _style = st; UpdatePreview(); };
            styles.Children.Add(b);
        }
        var styleRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var styleLabel = Label("Numbers", 92);
        styleLabel.VerticalAlignment = VerticalAlignment.Top;
        styleLabel.Margin = new Thickness(0, 5, 0, 0);
        DockPanel.SetDock(styleLabel, Dock.Left);
        styleRow.Children.Add(styleLabel);
        styleRow.Children.Add(styles);
        _edit.Children.Add(styleRow);

        _editPreview = Muted("", 0);
        _editPreview.Margin = new Thickness(92, 4, 0, 0);
        _editPreview.TextWrapping = TextWrapping.Wrap;
        _edit.Children.Add(_editPreview);
        _editError = new TextBlock { Foreground = Theme.GetBrush("Danger"), FontSize = 12.5, Margin = new Thickness(92, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _edit.Children.Add(_editError);
        foreach (var t in new[] { _from, _to, _refPage, _refNumber, _shift }) t.TextChanged += (_, _) => { _editError.Visibility = Visibility.Collapsed; UpdatePreview(); };
    }

    private void ShowEdit()
    {
        _title.Text = "Page numbers";
        _body.Children.Clear();
        _body.Children.Add(_edit);
        UpdatePreview();

        _buttons.Children.Clear();
        var next = Button("Next", accent: true, () => EditNext());
        var cancel = Button("Cancel", accent: false, () => _w.Close());
        var plain = Button("Plain 1, 2, 3 …", accent: false, () => UsePlain());
        plain.ToolTip = "Number every page by its place in the file";
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        next.Margin = new Thickness(8, 0, 0, 0);
        _buttons.Children.Add(next);
        _buttons.Children.Add(cancel);
        _buttons.Children.Add(plain);
        next.IsDefault = true;
    }

    /// <summary>"Page 14 is printed as 1" and "shift −13" say the same thing: keep them in step.</summary>
    private void SyncShift()
    {
        if (_syncing) return;
        if (!TryInt(_refPage, out int p) || !TryInt(_refNumber, out int r)) return;
        _syncing = true;
        _shift.Text = (r - p).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void SyncReads()
    {
        if (_syncing) return;
        if (!TryInt(_refPage, out int p) || !TryInt(_shift, out int k)) return;
        _syncing = true;
        _refNumber.Text = (p + k).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void UpdatePreview()
    {
        if (!TryInt(_from, out int a) || !TryInt(_to, out int b) || !TryInt(_refPage, out int p) || !TryInt(_refNumber, out int r)
            || a < 1 || b > _n || a > b)
        {
            _editPreview.Text = "";
            return;
        }
        string Reads(int page)
        {
            int num = r + (page - p);
            return num < 1 ? "below 1" : PageNumbering.Format(_style, "", num);
        }
        _editPreview.Text = a == b ? $"Page {a} will read {Reads(a)}." : $"Page {a} will read {Reads(a)}, page {b} will read {Reads(b)}.";
    }

    private void EditNext()
    {
        string? error = null;
        if (!TryInt(_from, out int a) || !TryInt(_to, out int b) || a < 1 || b > _n || a > b) error = $"Choose pages between 1 and {_n}.";
        else if (!TryInt(_refPage, out int p) || p < 1 || p > _n) error = $"The page has to be between 1 and {_n}.";
        else if (!TryInt(_refNumber, out int r) || Math.Abs(r) > 99999) error = "Type the number that page reads.";
        else
        {
            _pages = (PageNumber[])_now.Clone();
            _locked = new bool[_n];
            var prefix = _now[p - 1].IsNumbered && _now[p - 1].Style == _style ? _now[p - 1].Prefix : "";
            PageNumbering.Renumber(_pages, _locked, a - 1, b - 1, p - 1, r, _style, prefix);
            _history.Clear();
            Advance();
            return;
        }
        _editError.Text = error;
        _editError.Visibility = Visibility.Visible;
    }

    private void UsePlain()
    {
        _pages = PageNumbering.Plain(_n);
        _locked = Enumerable.Repeat(true, _n).ToArray();
        _history.Clear();
        Advance();
    }

    /// <summary>The next run of pages to ask about, or the result once every page has a number of its own.</summary>
    private void Advance()
    {
        var problem = PageNumbering.FirstProblem(_pages, _locked);
        if (problem != null) ShowQuestion(problem);
        else ShowResult();
    }

    private void Back()
    {
        if (_history.Count == 0) { ShowEdit(); return; }
        (_pages, _locked) = _history.Pop();
        Advance();
    }

    // ================================================================= step 2: pages that need a number of their own
    private void ShowQuestion(NumberingProblem pr)
    {
        string pages = Cap(PageNumbering.Pages(pr.From, pr.To));
        bool one = pr.From == pr.To;
        _title.Text = pr.Kind switch
        {
            NumberingProblemKind.BelowOne => $"{pages} would be numbered below 1",
            NumberingProblemKind.NoNumber => $"{pages} {(one ? "has" : "have")} no number",
            _ => pr.OtherFrom >= 0
                ? $"{pages} {(one ? "has" : "have")} the same {(one ? "number" : "numbers")} as {PageNumbering.Pages(pr.OtherFrom, pr.OtherTo)}"
                : $"{pages} share their numbers",
        };
        _body.Children.Clear();
        _body.Children.Add(Muted($"Number {PageNumbering.Pages(pr.From, pr.To)} like this:", 0));

        var choices = PageNumbering.Choices(_pages, pr);
        NumberingChoice? selected = null;
        var rows = new List<(Button Row, NumberingChoice? Choice)>();
        var next = Button("Next", accent: true, () => { });

        // "with a prefix": 1, 2, 3 … after text of the user's own, like A-1, A-2
        var prefixBox = new TextBox { Width = 64, Height = 26, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 8, 0), Text = "A-" };
        prefixBox.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        NumberingChoice PrefixChoice() => new("prefix", PageLabelStyle.Decimal, prefixBox.Text, 1);

        void Select(NumberingChoice? c)
        {
            selected = c;
            foreach (var (row, choice) in rows)
            {
                bool on = choice == null ? c != null && c.Title == "prefix" : ReferenceEquals(choice, c);
                if (on) row.Background = Theme.GetBrush("AccentSoft");
                else row.ClearValue(Control.BackgroundProperty);
            }
            next.IsEnabled = c != null && PageNumbering.Clash(_pages, _locked, pr.From, pr.To, c) == null;
        }

        Button OptionRow(UIElement left, string right, bool enabled, Action click)
        {
            var dock = new DockPanel { LastChildFill = true };
            var r = new TextBlock { Text = right, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.GetBrush("Muted"), FontSize = 12.5 };
            DockPanel.SetDock(r, Dock.Right);
            dock.Children.Add(r);
            dock.Children.Add(left);
            var b = new Button { Content = dock, IsEnabled = enabled, Opacity = enabled ? 1 : 0.5, Margin = new Thickness(0, 1, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            b.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
            b.Click += (_, _) => click();
            return b;
        }

        foreach (var c in choices)
        {
            var clash = PageNumbering.Clash(_pages, _locked, pr.From, pr.To, c);
            var text = new TextBlock { Text = c.Title, VerticalAlignment = VerticalAlignment.Center };
            var row = OptionRow(text, clash ?? PageNumbering.Preview(c, pr.From, pr.To), clash == null, () => Select(c));
            rows.Add((row, c));
            _body.Children.Add(row);
        }

        var prefixLeft = new StackPanel { Orientation = Orientation.Horizontal };
        prefixLeft.Children.Add(new TextBlock { Text = "With a prefix", VerticalAlignment = VerticalAlignment.Center });
        prefixLeft.Children.Add(prefixBox);
        var prefixRow = OptionRow(prefixLeft, "", true, () => Select(PrefixChoice()));
        var prefixRight = (TextBlock)((DockPanel)prefixRow.Content).Children[0];
        void UpdatePrefix()
        {
            var c = PrefixChoice();
            var clash = c.Prefix.Length == 0 ? "type a prefix" : PageNumbering.Clash(_pages, _locked, pr.From, pr.To, c);
            prefixRight.Text = clash ?? PageNumbering.Preview(c, pr.From, pr.To);
            if (selected?.Title == "prefix") Select(c.Prefix.Length == 0 ? null : c);
        }
        prefixBox.TextChanged += (_, _) => UpdatePrefix();
        prefixBox.GotKeyboardFocus += (_, _) => Select(PrefixChoice());
        rows.Add((prefixRow, null));
        _body.Children.Add(prefixRow);
        UpdatePrefix();

        _buttons.Children.Clear();
        var back = Button("Back", accent: false, Back);
        next.Click += (_, _) =>
        {
            if (selected == null || PageNumbering.Clash(_pages, _locked, pr.From, pr.To, selected) != null) return;
            _history.Push(((PageNumber[])_pages.Clone(), (bool[])_locked.Clone()));
            PageNumbering.Apply(_pages, _locked, pr.From, pr.To, selected);
            Advance();
        };
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(back, Dock.Right);
        next.Margin = new Thickness(8, 0, 0, 0);
        next.IsDefault = true;
        _buttons.Children.Add(next);
        _buttons.Children.Add(back);

        Select(choices.FirstOrDefault(c => PageNumbering.Clash(_pages, _locked, pr.From, pr.To, c) == null));
    }

    // ================================================================= step 3: the result
    private void ShowResult()
    {
        _title.Text = "New page numbers";
        _body.Children.Clear();
        var list = new StackPanel();
        var ranges = PageNumbering.Compress(_pages);
        for (int i = 0; i < ranges.Count; i++)
        {
            int from = ranges[i].Start, to = i + 1 < ranges.Count ? ranges[i + 1].Start - 1 : _n - 1;
            var row = new DockPanel { Height = 26 };
            var right = new TextBlock { Text = from == to ? _pages[from].Label : $"{_pages[from].Label} – {_pages[to].Label}", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.GetBrush("Text") };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = Cap(PageNumbering.Pages(from, to)), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.GetBrush("Text2") });
            list.Children.Add(row);
        }
        _body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var note = Muted("Saved into the PDF with your next save. Some viewers, such as web browsers, still show 1, 2, 3.", 0);
        note.Margin = new Thickness(0, 12, 0, 0);
        note.TextWrapping = TextWrapping.Wrap;
        _body.Children.Add(note);

        _buttons.Children.Clear();
        var apply = Button("Apply", accent: true, () => { _result = PageNumbering.Compress(_pages); _w.Close(); });
        var back = Button("Back", accent: false, Back);
        DockPanel.SetDock(apply, Dock.Right);
        DockPanel.SetDock(back, Dock.Right);
        apply.Margin = new Thickness(8, 0, 0, 0);
        apply.IsDefault = true;
        _buttons.Children.Add(apply);
        _buttons.Children.Add(back);
    }

    // ================================================================= small parts
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool TryInt(TextBox t, out int v) =>
        int.TryParse(t.Text.Trim().Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    private static TextBox NumberBox(string text, double width)
    {
        var t = new TextBox { Text = text, Width = width, Height = 28, Padding = new Thickness(6, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        t.GotKeyboardFocus += (_, _) => t.SelectAll();
        return t;
    }

    private static TextBlock Label(string text, double width, double left = 0)
    {
        var t = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 8, 0), Foreground = Theme.GetBrush("Text") };
        if (width > 0) t.Width = width;
        return t;
    }

    private static TextBlock Muted(string text, double left) =>
        new() { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0), Foreground = Theme.GetBrush("Muted") };

    private static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private static Button Button(string text, bool accent, Action click)
    {
        var b = new Button { Content = text, MinWidth = 84 };
        b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "AccentButton" : "PlainButton");
        b.Click += (_, _) => click();
        return b;
    }
}
