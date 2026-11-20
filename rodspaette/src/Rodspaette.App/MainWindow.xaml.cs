using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Rodspaette.Core;

namespace Rodspaette.App;

// ============================================================= row view models
public abstract class RowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>What the active document calls a page: its page label, or its plain number.</summary>
public static class PageName
{
    public static Func<int, string> Provider { get; set; } = i => (i + 1).ToString();
    public static string Of(int index) => Provider(index);
}

public sealed class OutlineRow : RowBase
{
    public required OutlineItem Item { get; init; }
    public int Depth { get; init; }
    public bool HasChildren => Item.Children.Count > 0;
    public bool Expanded { get; set; }
    public string Title => Item.Title.Length > 0 ? Item.Title : "(untitled)";
    public string PageLabel => Item.PageIndex >= 0 ? PageName.Of(Item.PageIndex) : "";
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public Visibility ChevronVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;
    public string Chevron => Expanded ? Icons.ChevronDown : Icons.ChevronRight;
    public FontWeight Weight => Depth == 0 ? FontWeights.SemiBold : (IsCurrent ? FontWeights.SemiBold : FontWeights.Normal);
    private bool _current;
    public bool IsCurrent { get => _current; set { if (_current != value) { _current = value; Changed(nameof(Background)); Changed(nameof(Foreground)); Changed(nameof(Weight)); } } }
    public Brush Background => IsCurrent ? Theme.GetBrush("AccentSoft") : Brushes.Transparent;
    public Brush Foreground => IsCurrent ? Theme.GetBrush("Accent") : Theme.GetBrush("Text");
}

public sealed class BookmarkRow
{
    public required OutlineItem Item { get; init; }
    public string Title => Item.Title;
    public string PageLabel => $"Page {PageName.Of(Item.PageIndex)}";
}

public sealed class MarkupRow
{
    public required AnnotInfo Info { get; init; }
    public string Label => Info.Kind switch
    {
        AnnotKind.Highlight => "Highlight",
        AnnotKind.Underline => "Underline",
        AnnotKind.StrikeOut => "Strikethrough",
        AnnotKind.Squiggly => "Squiggly underline",
        AnnotKind.Ink => "Pen",
        AnnotKind.Note => "Note",
        AnnotKind.FreeText => "Text box",
        _ => "Markup",
    } + $" · p. {PageName.Of(Info.PageIndex)}";
    public string Text => Info.Kind is AnnotKind.Note or AnnotKind.FreeText ? Info.Contents
        : !string.IsNullOrWhiteSpace(Info.MarkedText) ? Info.MarkedText
        : Info.Kind == AnnotKind.Ink ? "Drawing"
        : Info.Kind == AnnotKind.Highlight ? "Highlighted area"
        : Info.Contents;
    /// <summary>The comment on a highlight (or underline, strikethrough, drawing), shown under its text.</summary>
    public string Comment => PdfViewer.CommentOf(Info);
    public Visibility CommentVisibility => Comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush Swatch => new SolidColorBrush(Color.FromArgb(255, (byte)(Info.ColorArgb >> 16), (byte)(Info.ColorArgb >> 8), (byte)Info.ColorArgb));
}

public sealed class HitRow : RowBase
{
    public required SearchHit Hit { get; init; }
    public string PageLabel => $"Page {PageName.Of(Hit.PageIndex)}";
    public string Before => Hit.Before;
    public string Match => Hit.Match;
    public string After => Hit.After;
    public Visibility OcrVisibility => Hit.FromOcr ? Visibility.Visible : Visibility.Collapsed;
    private bool _current;
    public bool IsCurrent { get => _current; set { if (_current != value) { _current = value; Changed(nameof(Background)); } } }
    public Brush Background => IsCurrent ? Theme.GetBrush("AccentSoft") : Brushes.Transparent;
}

public sealed class TabRow
{
    public required DocumentTab Tab { get; init; }
    /// <summary>Being dragged to a new place in the list.</summary>
    public bool Dragged { get; init; }
    public Brush Background => Dragged ? Theme.GetBrush("AccentSoft") : Tab.IsActive ? Theme.GetBrush("Hover") : Brushes.Transparent;
    public FontWeight Weight => Tab.IsActive ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush IconBrush => Tab.IsActive ? Theme.GetBrush("Accent") : Theme.GetBrush("Muted");
}

public sealed class RecentRow
{
    public required string Path { get; init; }
    public required DateTime Opened { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string When
    {
        get
        {
            var d = DateTime.Today - Opened.Date;
            if (d.TotalDays < 1) return Opened.ToString("HH:mm");
            if (d.TotalDays < 2) return "Yesterday";
            if (d.TotalDays < 7) return Opened.ToString("ddd");
            return Opened.ToString("d MMM");
        }
    }
}

// ============================================================= main window
public partial class MainWindow : Window
{
    private readonly AppSettings _s;
    private System.Windows.Shell.WindowChrome Chrome => System.Windows.Shell.WindowChrome.GetWindowChrome(this);
    private readonly List<DocumentTab> _tabs = new();
    private DocumentTab? _active;
    private readonly TaskbarTabs _taskbar;
    private uint _taskbarCreatedMsg;

    private bool _panelsHidden;         // Ctrl+H: pinned side panels retracted
    private bool _wantFullScreen;       // full-screen button on the toolbar
    private bool _leftVisible, _tabsPinned, _leftPeek, _tabsPeek;
    private string _leftView = "contents";
    private bool _searchMode;
    private bool _fullScreen;
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private bool _closeConfirmed;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _markupDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private Tool _tool = Tool.Select;
    private string _colorKey = "highlight";
    private static readonly string[] Palette = { "#FFD60A", "#34C759", "#5AC8FA", "#FF6B9A", "#FF9F0A", "#E5341F", "#0066CC", "#1D1D1F" };

    private CancellationTokenSource? _searchCts;
    private readonly List<SearchHit> _hits = new();
    private readonly ObservableCollection<HitRow> _hitRows = new();
    private int _hitIndex = -1;

    private readonly ObservableCollection<OutlineRow> _outlineRows = new();
    private readonly HashSet<OutlineItem> _expanded = new();
    private string _markupFilter = "all";
    private List<AnnotInfo> _allMarkup = new();
    private int _markupGen;

    public MainWindow(AppSettings settings)
    {
        _s = settings;
        Shortcuts.Load(_s);   // before the window is built: its menus and tool tips show the keys
        _typing.Changed += () => { UpdateToolbarGlass(); _lookUp?.UpdateGlass(); };
        PageName.Provider = i => _active?.LabelOf(i) ?? (i + 1).ToString();
        InitializeComponent();
        // pinned panels wait until the opening animation has gone, so the logo stays centred; then they slide in
        StartVersion.Text = "Version " + AppInfo.Version;
        _splashActive = Splash.Attach(this, Root, StartLogo, () =>
        {
            _splashActive = false;
            ApplyChrome();
            SlideInDockedPanel();
            ShowStartVersion(animate: true);
        });
        if (!_splashActive) ShowStartVersion(animate: false);
        UpdateSaveButton();
        InitMouseMenu();
        InitHomeMenu();
        InitTabDrag();
        _taskbar = new TaskbarTabs(this);
        _taskbar.ActivateRequested += t => { BringToFront(); Activate(t); };
        _taskbar.CloseRequested += async t => await CloseTab(t);

        // "Open where I left off" off: start with the default layout (nothing pinned, toolbar at home)
        _leftVisible = _s.RestoreSession && _s.LeftPanelVisible;
        _tabsPinned = _s.RestoreSession && _s.TabsPinned;
        _leftView = _s.LeftView is "contents" or "bookmarks" or "markup" ? _s.LeftView : "contents";

        if (!double.IsNaN(_s.WinLeft) && !double.IsNaN(_s.WinTop))
        {
            Left = _s.WinLeft; Top = _s.WinTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = Math.Max(MinWidth, _s.WinWidth);
        Height = Math.Max(MinHeight, _s.WinHeight);
        if (_s.Maximized) WindowState = WindowState.Maximized;

        OutlineList.ItemsSource = _outlineRows;
        SearchList.ItemsSource = _hitRows;
        ColorList.ItemsSource = Palette;
        HighlightSwatch.Fill = BrushFrom(_s.ToolColors["highlight"]);

        _peekClose.Tick += (_, _) => PeekCloseTick();
        if (_leftVisible && _tabsPinned) _tabsPinned = false;   // one pinned panel at a time
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RunSearch(); };
        _markupDebounce.Tick += (_, _) => { _markupDebounce.Stop(); LoadMarkup(); };

        switch (Theme.Current)
        {
            case AppTheme.Dark: ThemeDark.IsChecked = true; break;
            case AppTheme.Green: ThemeGreen.IsChecked = true; break;
            default: ThemeLight.IsChecked = true; break;
        }
        TintToggle.IsChecked = Theme.TintPages;

        (_leftView switch { "bookmarks" => ViewBookmarks, "markup" => ViewMarkup, _ => ViewContents }).IsChecked = true;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, e) =>
        {
            if (_lookUp?.IsOpen != true || _lookUp.IsMouseOver || (_lookUp.IsDocked && Toolbar.IsMouseOver)) return;
            _lookUp.Close();
            if (e.ChangedButton != MouseButton.Right) e.Handled = true;
        };
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        StateChanged += (_, _) => UpdateMaxIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;

        RefreshRecent();
        UpdateDocChrome();
        ApplyChrome();
    }

    // ============================================================= window plumbing
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarButtonCreated");
        int round = 2; // DWMWCP_ROUND (Windows 11)
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Win32.SetDwmBool(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.Current == AppTheme.Dark);
    }

    private const int WM_MOUSEHWHEEL = 0x020E;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_GETMINMAXINFO)
        {
            // keep maximised windows inside the work area (WindowStyle=None would otherwise cover the taskbar)
            var mmi = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
            var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref info))
            {
                var w = info.rcWork; var m = info.rcMonitor;
                mmi.ptMaxPosition.X = w.Left - m.Left;
                mmi.ptMaxPosition.Y = w.Top - m.Top;
                mmi.ptMaxSize.X = w.Right - w.Left;
                mmi.ptMaxSize.Y = w.Bottom - w.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            handled = false;
        }
        else if (msg == WM_MOUSEHWHEEL)
        {
            // WPF ignores sideways wheel messages (touchpad two-finger swipe, tilt wheel): pass them to the page view
            if (_active != null && _active.Viewer.IsMouseOver)
            {
                int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                _active.Viewer.HorizontalWheel(delta);
                handled = true;
                return new IntPtr(1);   // some touchpad drivers stop sending sideways scrolls unless this is non-zero
            }
        }
        else if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            _taskbar.Attach();
            _taskbar.Reregister(_tabs, _active);
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _net.Changed += () => OfflineIndicator.Visibility = _net.IsOnline ? Visibility.Collapsed : Visibility.Visible;
        _net.Start(Dispatcher);
        OfflineIndicator.Visibility = _net.IsOnline ? Visibility.Collapsed : Visibility.Visible;
        // put the toolbar where it was left, and keep it inside the reading area when the window changes size
        if (_s.RestoreSession) Dispatcher.InvokeAsync(() => SetToolbarOffset(_s.ToolbarX, _s.ToolbarY), DispatcherPriority.Loaded);
        ViewerHost.SizeChanged += (_, _) => { if (!_tbDrag) SetToolbarOffset(ToolbarShift.X, ToolbarShift.Y); _lookUp?.Reposition(); };
        Toolbar.MouseEnter += (_, _) => UpdateToolbarGlass();
        Toolbar.MouseLeave += (_, _) => { _typing.MouseLeft(); UpdateToolbarGlass(); };
        UpdateToolbarGlass();
        _taskbar.Attach();
        foreach (var t in _tabs) _taskbar.Add(t);
        Dispatcher.InvokeAsync(AskLastActiveClick, DispatcherPriority.ApplicationIdle);
    }

    private void UpdateMaxIcon()
    {
        MaxIcon.Data = WindowState == WindowState.Maximized ? Icons.Restore : Icons.Maximize;
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _s.Maximized ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetForegroundWindow(hwnd);
    }

    private void AskLastActiveClick()
    {
        if (_s.AskedLastActiveClick) return;
        _s.AskedLastActiveClick = true;
        _s.Save();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
            if (key?.GetValue("LastActiveClick") is int v && v == 1) return;
        }
        catch { }
        int r = Dialogs.Ask(this, "Open your last tab with one click on the taskbar?",
            "This turns on a Windows setting for all apps. It applies after you sign out and back in.",
            "Turn on", "Not now");
        if (r != 0) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: true);
            key.SetValue("LastActiveClick", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Dialogs.Info(this, "The setting could not be changed", ex.Message); }
    }

    // ============================================================= opening
    public void OpenFromOtherInstance(string[] paths)
    {
        BringToFront();
        if (paths.Length > 0) OpenFiles(paths);
    }

    /// <summary>At launch: the files open last time (if "Open where I left off" is on), then any files passed in.</summary>
    public void OpenAtStartup(IEnumerable<string> paths)
    {
        var list = new List<string>();
        if (_s.RestoreSession)
        {
            list.AddRange(_s.SessionFiles.Where(f => !string.Equals(f, _s.SessionActive, StringComparison.OrdinalIgnoreCase)));
            if (_s.SessionActive != null) list.Add(_s.SessionActive);   // opened last, so it's the one in front
        }
        list.AddRange(paths);
        var open = list.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // a file named on the command line goes in front even if it was also open last time
        var front = paths.LastOrDefault(File.Exists);
        if (front != null) { open.RemoveAll(f => string.Equals(f, front, StringComparison.OrdinalIgnoreCase)); open.Add(front); }
        if (open.Count > 0) OpenFiles(open);
    }

    public bool RestoreSession
    {
        get => _s.RestoreSession;
        set { _s.RestoreSession = value; _s.Save(); }
    }

    public async void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths) await OpenFile(p);
    }

    private void Menu_Open(object sender, RoutedEventArgs e)
    {
        TitleMenu.IsOpen = false;
        ShowOpenDialog();
    }

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog { Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*", Multiselect = true };
        if (_active != null) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_active.FilePath);
        if (dlg.ShowDialog(this) == true) OpenFiles(dlg.FileNames);
    }

    private async Task OpenFile(string path)
    {
        try { path = System.IO.Path.GetFullPath(path); } catch { return; }
        var existing = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { Activate(existing); return; }
        if (!File.Exists(path)) { Dialogs.Info(this, "File not found", path); return; }

        byte[] bytes;
        try { bytes = await Task.Run(() => File.ReadAllBytes(path)); }
        catch (Exception ex) { Dialogs.Info(this, "Could not open the file", ex.Message); return; }

        PdfDocument doc;
        string? password = null;
        while (true)
        {
            try { doc = await PdfWorker.Shared.Run(() => PdfDocument.Open(bytes, password)); break; }
            catch (PdfPasswordRequiredException ex)
            {
                password = Dialogs.Input(this, ex.WrongPassword ? "Wrong password. Try again" : $"Password for {System.IO.Path.GetFileName(path)}", "", "Open", password: true);
                if (password == null) return;
            }
            catch (Exception ex) { Dialogs.Info(this, "Could not open " + System.IO.Path.GetFileName(path), ex.Message); return; }
        }
        if (doc.PageCount == 0) { Dialogs.Info(this, "This PDF has no pages", path); await PdfWorker.Shared.Run(() => doc.Dispose()); return; }

        List<OutlineItem> outline;
        try { outline = await PdfWorker.Shared.Run(() => doc.GetOutline()); }
        catch { outline = new List<OutlineItem>(); }
        var (chapters, bookmarks) = OutlineWriter.Split(outline);

        string[]? labels;
        try { labels = await PdfWorker.Shared.Run(() => doc.PageLabels()); }
        catch { labels = null; }

        var tab = new DocumentTab(doc, path, _s) { Chapters = chapters, Bookmarks = bookmarks };
        tab.LoadedLabels(labels);
        if (CountOutline(chapters) <= 80) MarkAllExpanded(chapters);
        else foreach (var c in chapters) if (c.IsOpen) _expanded.Add(c);

        var v = tab.Viewer;
        v.Tool = _tool;
        v.PageChanged += p => OnPageChanged(tab, p);
        v.ZoomChanged += () => { if (tab == _active) UpdateZoomLabel(); };
        v.Edited += p => OnEdited(tab, p);
        v.Hint += ShowToast;
        v.MenuExtras += AddMenuExtras;
        v.LookUpRequested += OpenLookUp;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DocumentTab.Dirty) && tab == _active) UpdateDocChrome();
            if (e.PropertyName is nameof(DocumentTab.Name)) { _taskbar.Rename(tab); if (tab == _active) UpdateDocChrome(); }
        };
        int last = _s.LastPage(path);
        if (last > 0 && last < doc.PageCount) v.SetInitialPage(last);

        _tabs.Add(tab);
        _taskbar.Add(tab);
        Activate(tab);
        _s.Touch(path, last);
        _s.Save();
        RefreshRecent();
        _ = RunOcr(tab);
    }

    private static int CountOutline(List<OutlineItem> items) => items.Sum(i => 1 + CountOutline(i.Children));
    private void MarkAllExpanded(List<OutlineItem> items) { foreach (var i in items) { if (i.Children.Count > 0) _expanded.Add(i); MarkAllExpanded(i.Children); } }

    private async Task RunOcr(DocumentTab tab)
    {
        try
        {
            await OcrService.RunAsync(tab,
                page => { tab.Viewer.RefreshPage(page); if (_searchMode && tab == _active) _searchDebounce.Start(); },
                status => tab.OcrStatus = status);
        }
        catch { tab.OcrStatus = ""; }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            OpenFiles(files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || File.Exists(f)));
    }

    // ============================================================= tabs
    private void Activate(DocumentTab tab)
    {
        LeaveHome();
        if (_active == tab) { tab.Viewer.FocusView(); return; }
        if (_active != null)
        {
            _active.IsActive = false;
            _active.Viewer.CancelEditor();
            ViewerHost.Children.Remove(_active.Viewer);
            _s.Touch(_active.FilePath, _active.CurrentPage);
        }
        _active = tab;
        _recentTabs.Remove(tab);
        _recentTabs.Insert(0, tab);
        tab.IsActive = true;
        ViewerHost.Children.Insert(0, tab.Viewer);
        EmptyState.Visibility = Visibility.Collapsed;
        tab.Viewer.Tool = _tool;
        if (_searchMode) CloseSearch();
        UpdateDocChrome();
        UpdateZoomLabel();
        RebuildOutline();
        RefreshBookmarks();
        if (_leftView == "markup") LoadMarkup();
        RefreshTabs();
        _taskbar.SetActive(tab);
        Dispatcher.InvokeAsync(() => tab.Viewer.FocusView(), DispatcherPriority.Input);
    }

    private void ShowEmpty()
    {
        _active = null;
        _home = false;
        HomeBackButton.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        _outlineRows.Clear();
        BookmarkList.ItemsSource = null;
        MarkupList.ItemsSource = null;
        RefreshRecent();
        RefreshTabs();
        UpdateDocChrome();
    }

    private void RefreshTabs()
    {
        TabsList.ItemsSource = _tabs.Select(t => new TabRow { Tab = t, Dragged = t == _dragTab && _tabDragging }).ToList();
    }

    // ---------------------------------------------------- drag a tab up or down the list to reorder the tabs
    private DocumentTab? _dragTab;
    private Point _dragStart;
    private bool _tabDragging;

    private void InitTabDrag()
    {
        TabsList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragTab = RowAt(e.OriginalSource as DependencyObject)?.Tab;
            _dragStart = e.GetPosition(TabsList);
            _tabDragging = false;
        };
        TabsList.PreviewMouseMove += (_, e) =>
        {
            if (_dragTab == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragTab = null; return; }
            var p = e.GetPosition(TabsList);
            if (!_tabDragging)
            {
                if (Math.Abs(p.Y - _dragStart.Y) < 6 || _tabs.Count < 2) return;
                _tabDragging = true;
                TabsList.CaptureMouse();   // takes the press from the row, so letting go doesn't open or close a tab
                Mouse.OverrideCursor = Cursors.SizeNS;
                RefreshTabs();
            }
            MoveDraggedTab(p.Y);
            e.Handled = true;
        };
        TabsList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_tabDragging) { e.Handled = true; TabsList.ReleaseMouseCapture(); }
            else _dragTab = null;
        };
        TabsList.LostMouseCapture += (_, _) =>
        {
            if (!_tabDragging) return;
            _tabDragging = false;
            _dragTab = null;
            Mouse.OverrideCursor = null;
            RefreshTabs();
            _taskbar.Reorder(_tabs);
        };
    }

    private static TabRow? RowAt(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: TabRow row }) return row;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Puts the dragged tab where the mouse is: in the place of the row under it.</summary>
    private void MoveDraggedTab(double y)
    {
        if (_dragTab == null) return;
        int from = _tabs.IndexOf(_dragTab);
        if (from < 0) return;
        int to = _tabs.Count - 1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (TabsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            if (y < c.TranslatePoint(new Point(0, 0), TabsList).Y + c.ActualHeight) { to = i; break; }
        }
        if (to == from) return;
        _tabs.RemoveAt(from);
        _tabs.Insert(to, _dragTab);
        RefreshTabs();
        TabsList.UpdateLayout();
    }

    private void RefreshRecent()
    {
        var open = new HashSet<string>(_tabs.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        var rows = _s.Recent.Where(r => !open.Contains(r.Path) && File.Exists(r.Path))
            .Take(12).Select(r => new RecentRow { Path = r.Path, Opened = r.Opened }).ToList();
        RecentList.ItemsSource = rows;
        EmptyRecent.ItemsSource = rows.Take(6).ToList();
    }

    private void TabRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabRow row) Activate(row.Tab);
    }

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is TabRow row) await CloseTab(row.Tab);
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentRow row) OpenFiles(new[] { row.Path });
    }

    private async Task<bool> CloseTab(DocumentTab tab)
    {
        if (tab.Dirty)
        {
            Activate(tab);
            int r = Dialogs.Ask(this, $"Save changes to {tab.Name}?", "", "Save", "Don't save", "Cancel");
            if (r == 0) { if (!await Save(tab, false)) return false; }
            else if (r != 1) return false;
        }
        tab.OcrCancel.Cancel();
        _s.Touch(tab.FilePath, tab.CurrentPage);
        _taskbar.Remove(tab);
        int idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _recentTabs.Remove(tab);
        if (_active == tab)
        {
            ViewerHost.Children.Remove(tab.Viewer);
            _active = null;
            if (_tabs.Count > 0) Activate(_tabs[Math.Clamp(idx, 0, _tabs.Count - 1)]);
            else ShowEmpty();
        }
        RefreshTabs();
        RefreshRecent();
        var doc = tab.Doc;
        try { await PdfWorker.Shared.Run(() => doc.Dispose()); } catch { }
        _s.Save();
        return true;
    }

    /// <summary>Tabs, most recently used first (for "Last-used tab").</summary>
    private readonly List<DocumentTab> _recentTabs = new();

    /// <summary>Back to the tab used before this one; pressed again, it comes back.</summary>
    private void SwitchToLastTab()
    {
        var t = _recentTabs.FirstOrDefault(x => x != _active && _tabs.Contains(x));
        if (t != null) Activate(t);
    }

    private void CycleTabs(int dir)
    {
        if (_tabs.Count < 2 || _active == null) return;
        int i = (_tabs.IndexOf(_active) + dir + _tabs.Count) % _tabs.Count;
        Activate(_tabs[i]);
    }

    // ============================================================= document chrome
    private void UpdateDocChrome()
    {
        var t = _active;
        DocTitle.Text = t?.Name ?? "Rødspætte PDF";
        Title = t != null ? $"{t.Name} — Rødspætte PDF" : "Rødspætte PDF";
        EditedLabel.Visibility = t?.Dirty == true ? Visibility.Visible : Visibility.Collapsed;
        UnsavedBox.Visibility = t?.Dirty == true ? Visibility.Visible : Visibility.Collapsed;
        ShowPageNumber(t);
        SaveButton.IsEnabled = PrintButton.IsEnabled = SearchButton.IsEnabled = t != null;
        HomeToggle.IsChecked = _home || t == null;
        UpdateToolbarCompact();
        _lookUp?.Reposition();
        UpdateBookmarkState();
    }

    /// <summary>The page box shows what the page is called; with page labels, its place in the file goes beside it, "(14 / 300)".</summary>
    private void ShowPageNumber(DocumentTab? t)
    {
        if (t == null) { PageCountText.Text = "/ 0"; if (!PageBox.IsKeyboardFocused) PageBox.Text = ""; return; }
        int n = t.Doc.PageCount;
        PageCountText.Text = t.LabelTexts != null ? $"({t.CurrentPage + 1} / {n})" : $"/ {n}";
        if (!PageBox.IsKeyboardFocused) PageBox.Text = t.LabelOf(t.CurrentPage);
        int longest = t.LabelTexts?.Max(l => l.Length) ?? n.ToString().Length;
        PageBox.Width = Math.Clamp(18 + 8 * longest, 46, 110);
    }

    private void UpdateZoomLabel()
    {
        if (_active == null) return;
        ZoomText.Text = $"{Math.Round(_active.Viewer.Zoom * 100)}%";
        ZoomLabel.ToolTip = _active.Viewer.FitWidth ? "Fitting width" : "Fit width";
    }

    private void OnPageChanged(DocumentTab tab, int page)
    {
        if (tab != _active) return;
        ShowPageNumber(tab);
        UpdateBookmarkState();
        HighlightCurrentChapter();
    }

    private void OnEdited(DocumentTab tab, int page)
    {
        MarkDirty(tab);
        _taskbar.Invalidate(tab);
        if (tab == _active)
        {
            UpdateDocChrome();
            if (_leftView == "markup") _markupDebounce.Start();
        }
    }

    private void ShowToast(string text) => ShowToast(text, sticky: false);

    /// <summary>A sticky toast stays until <see cref="HideToast"/> or the next toast.</summary>
    private void ShowToast(string text, bool sticky)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        if (!sticky) _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    // ============================================================= chrome: panels, Ctrl+H, edges
    /// <summary>A side panel's width, and its gap from the window's left edge while it floats. Docked, the panel
    /// keeps its place: its column is the gap wider and the gap becomes panel-coloured space on its left.</summary>
    private const double PanelWidth = 320, PanelGap = 14;

    private void ApplyChrome()
    {
        bool title = !_wantFullScreen;
        TitleBar.Visibility = title ? Visibility.Visible : Visibility.Collapsed;
        TitleRow.Height = new GridLength(title ? 48 : 0);
        Chrome.CaptionHeight = title ? 48 : 0;

        bool leftDock = LeftDocked;
        bool tabsDock = TabsDocked;
        if (leftDock) _leftPeek = false;
        if (tabsDock) _tabsPeek = false;
        if (_leftPeek && _tabsPeek) _tabsPeek = false;

        // ---- left panel
        if (leftDock)
        {
            LeftPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(LeftPanel, 0);
            Grid.SetColumnSpan(LeftPanel, 1);
            LeftPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            LeftPanel.Width = double.NaN;
            LeftPanel.Margin = new Thickness(0);
            LeftPanel.Padding = new Thickness(PanelGap + 1, 0, 0, 0);   // + the floating card's left border
            LeftPanel.CornerRadius = new CornerRadius(0);
            LeftPanel.BorderThickness = new Thickness(0, 0, 1, 0);
            LeftPanel.Effect = null;
            LeftPanel.SetResourceReference(Border.BackgroundProperty, "B.Panel");
            LeftCol.Width = new GridLength(PanelGap + PanelWidth);
        }
        else if (_leftPeek)
        {
            LeftPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(LeftPanel, 0);
            Grid.SetColumnSpan(LeftPanel, 3);
            LeftPanel.HorizontalAlignment = HorizontalAlignment.Left;
            LeftPanel.Width = PanelWidth;
            LeftPanel.Margin = new Thickness(PanelGap, 8, 8, 8);
            LeftPanel.Padding = new Thickness(0);
            LeftPanel.CornerRadius = new CornerRadius(14);
            LeftPanel.BorderThickness = new Thickness(1);
            LeftPanel.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.2 };
            LeftPanel.SetResourceReference(Border.BackgroundProperty, "B.SbBg");
            if (!tabsDock) LeftCol.Width = new GridLength(0);
        }
        else
        {
            LeftPanel.Visibility = Visibility.Collapsed;
            if (!tabsDock) LeftCol.Width = new GridLength(0);
        }

        // ---- tabs panel (also on the left now)
        if (tabsDock)
        {
            TabsPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(TabsPanel, 0);
            Grid.SetColumnSpan(TabsPanel, 1);
            TabsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            TabsPanel.Width = double.NaN;
            TabsPanel.Margin = new Thickness(0);
            TabsPanel.Padding = new Thickness(10 + PanelGap + 1, 12, 10, 12);
            TabsPanel.CornerRadius = new CornerRadius(0);
            TabsPanel.BorderThickness = new Thickness(0, 0, 1, 0);
            TabsPanel.Effect = null;
            LeftCol.Width = new GridLength(PanelGap + PanelWidth);
        }
        else if (_tabsPeek)
        {
            TabsPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(TabsPanel, 0);
            Grid.SetColumnSpan(TabsPanel, 3);
            TabsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            TabsPanel.Width = PanelWidth;
            TabsPanel.Margin = new Thickness(PanelGap, 8, 8, 8);
            TabsPanel.Padding = new Thickness(10, 12, 10, 12);
            TabsPanel.CornerRadius = new CornerRadius(14);
            TabsPanel.BorderThickness = new Thickness(1);
            TabsPanel.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.2 };
        }
        else TabsPanel.Visibility = Visibility.Collapsed;
        Panel.SetZIndex(TabsPanel, _tabsPeek ? 22 : 20);
        Panel.SetZIndex(LeftPanel, _leftPeek ? 22 : 20);
        RightCol.Width = new GridLength(0);

        // the touch bars: accent when their panel is open, wider while the mouse is on them
        SetPill(ContentsPill, leftDock || _leftPeek, ContentsBar.IsMouseOver);
        SetPill(TabsPill, tabsDock || _tabsPeek, TabsBar.IsMouseOver);

        LeftPinToggle.IsChecked = LeftPinShown;
        PinToggle.IsChecked = TabsPinShown;
        FullScreenToggle.IsChecked = _wantFullScreen;

        SetFullScreen(_wantFullScreen);
    }

    private void SetPill(Border pill, bool open, bool hover)
    {
        pill.Width = hover ? 6 : 4;
        pill.SetResourceReference(Border.BackgroundProperty, open || hover ? "B.Accent" : "B.Edge");
        pill.Opacity = open || hover ? 1 : 0.9;
    }

    // a panel is docked when it's pinned, not retracted with Ctrl+H, and the window isn't in full screen
    private bool _splashActive;
    private bool LeftDocked => _leftVisible && !_panelsHidden && !_wantFullScreen && !_splashActive;
    private bool TabsDocked => _tabsPinned && !_panelsHidden && !_wantFullScreen && !_splashActive;

    /// <summary>Slides the docked panel in from the left edge, pushing the page aside.</summary>
    /// <summary>The start page's version line appears only once the opening animation has finished.</summary>
    private void ShowStartVersion(bool animate)
    {
        StartVersion.Visibility = Visibility.Visible;
        if (!animate) return;
        StartVersion.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
    }

    private void SlideInDockedPanel()
    {
        double target = LeftCol.Width.Value;
        if (target <= 0) return;
        Border panel = LeftDocked ? LeftPanel : TabsPanel;
        panel.Width = target;
        panel.HorizontalAlignment = HorizontalAlignment.Right;   // slides rather than squeezes while the column grows
        LeftCol.Width = new GridLength(0);
        var clock = Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 300);
            LeftCol.Width = new GridLength(target * (1 - Math.Pow(1 - t, 3)));
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            panel.Width = double.NaN;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            LeftCol.Width = new GridLength(target);
        };
        CompositionTarget.Rendering += tick;
    }
    private bool LeftPinShown => _leftVisible && !_panelsHidden;
    private bool TabsPinShown => _tabsPinned && !_panelsHidden;

    /// <summary>Ctrl+H: retract the pinned side panels, or bring them back. The title bar stays.</summary>
    private void TogglePanels()
    {
        if (_panelsHidden) _panelsHidden = false;
        else if (_leftVisible || _tabsPinned) _panelsHidden = true;
        _leftPeek = _tabsPeek = false;
        TitleMenu.IsOpen = AppearancePopup.IsOpen = false;
        ApplyChrome();
        _active?.Viewer.FocusView();
    }

    private void ToggleFullScreen(bool on)
    {
        _wantFullScreen = on;
        _leftPeek = _tabsPeek = false;
        TitleMenu.IsOpen = AppearancePopup.IsOpen = false;
        ApplyChrome();
        _active?.Viewer.FocusView();
    }

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        if (on)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var hwnd = new WindowInteropHelper(this).Handle;
            var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (mon == IntPtr.Zero || !Win32.GetMonitorInfo(mon, ref info)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            _fullScreen = true;
            Chrome.ResizeBorderThickness = new Thickness(0);
            WindowState = WindowState.Normal;
            Left = info.rcMonitor.Left / dpi.DpiScaleX;
            Top = info.rcMonitor.Top / dpi.DpiScaleY;
            Width = (info.rcMonitor.Right - info.rcMonitor.Left) / dpi.DpiScaleX;
            Height = (info.rcMonitor.Bottom - info.rcMonitor.Top) / dpi.DpiScaleY;
        }
        else
        {
            _fullScreen = false;
            Chrome.ResizeBorderThickness = new Thickness(6);
            if (!_restoreBounds.IsEmpty)
            {
                Left = _restoreBounds.Left; Top = _restoreBounds.Top;
                Width = _restoreBounds.Width; Height = _restoreBounds.Height;
            }
            WindowState = _restoreState;
        }
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => ResetToolbar();
    private void FullScreenToggle_Click(object sender, RoutedEventArgs e) => ToggleFullScreen(!_wantFullScreen);

    // one panel is pinned at a time: pinning one unpins the other (which stays reachable through its touch bar).
    // Pinning while the panels are retracted with Ctrl+H brings them back.
    private void LeftToggle_Click(object sender, RoutedEventArgs e)
    {
        bool pin = !LeftPinShown;
        _panelsHidden = false;
        _leftVisible = pin;
        if (pin) _tabsPinned = false;
        _leftPeek = _tabsPeek = false;
        SavePins();
        ApplyChrome();
    }

    private void TabsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool pin = !TabsPinShown;
        _panelsHidden = false;
        _tabsPinned = pin;
        if (pin) _leftVisible = false;
        _leftPeek = _tabsPeek = false;
        SavePins();
        ApplyChrome();
    }

    private void SavePins()
    {
        _s.LeftPanelVisible = _leftVisible;
        _s.TabsPinned = _tabsPinned;
        _s.Save();
    }

    // ---------------------------------------------------- bottom toolbar: drag anywhere, Home puts it back
    private bool _tbDrag, _tbMoved;
    private Point _tbStart;
    private Vector _tbOrigin;

    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // buttons and the page box handle their own clicks, so this only fires on the toolbar's background
        _tbDrag = true;
        _tbMoved = false;
        _tbStart = e.GetPosition(ViewerHost);
        ToolbarShift.BeginAnimation(TranslateTransform.XProperty, null);
        ToolbarShift.BeginAnimation(TranslateTransform.YProperty, null);
        _tbOrigin = new Vector(ToolbarShift.X, ToolbarShift.Y);
        Toolbar.CaptureMouse();
        e.Handled = true;
    }

    private void Toolbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_tbDrag) return;
        var d = e.GetPosition(ViewerHost) - _tbStart;
        if (!_tbMoved && d.Length < 3) return;
        _tbMoved = true;
        Mouse.OverrideCursor = Cursors.SizeAll;
        SetToolbarOffset(_tbOrigin.X + d.X, _tbOrigin.Y + d.Y);
    }

    private void Toolbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tbDrag) return;
        Toolbar.ReleaseMouseCapture();
    }

    private void Toolbar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_tbDrag) return;
        _tbDrag = false;
        Mouse.OverrideCursor = null;
        if (_tbMoved) { _s.ToolbarX = ToolbarShift.X; _s.ToolbarY = ToolbarShift.Y; _s.Save(); }
        UpdateToolbarGlass();
    }

    /// <summary>Moves the toolbar from its home spot (centred at the bottom), keeping it inside the reading area.</summary>
    private void SetToolbarOffset(double x, double y)
    {
        double w = ViewerHost.ActualWidth, h = ViewerHost.ActualHeight;
        double tw = Toolbar.ActualWidth, th = Toolbar.ActualHeight;
        if (w > 0 && tw > 0)
        {
            double homeLeft = (w - tw) / 2, homeTop = h - Toolbar.Margin.Bottom - th, drop = ToolbarDrop.Y;
            x = Math.Clamp(x, -homeLeft + 4, Math.Max(-homeLeft + 4, homeLeft - 4));
            y = Math.Clamp(y, -homeTop - drop + 4, Math.Max(-homeTop - drop + 4, Toolbar.Margin.Bottom - drop - 4));
        }
        ToolbarShift.X = x;
        ToolbarShift.Y = y;
        _lookUp?.Reposition();
    }

    private void ResetToolbar()
    {
        double fx = ToolbarShift.X, fy = ToolbarShift.Y;
        ToolbarShift.X = ToolbarShift.Y = 0;
        _s.ToolbarX = _s.ToolbarY = 0;
        _s.Save();
        _lookUp?.Reposition();
        if (Math.Abs(fx) + Math.Abs(fy) < 0.5 || _lookUp?.IsDocked == true) return;
        var dur = TimeSpan.FromSeconds(0.28);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ToolbarShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fx, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        ToolbarShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fy, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    // ---------------------------------------------------- left-edge touch bars
    private readonly DispatcherTimer _peekClose = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private void ContentsBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _peekClose.Stop();
        if (!LeftDocked) { _leftPeek = true; _tabsPeek = false; }
        ApplyChrome();
    }

    private void TabsBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _peekClose.Stop();
        if (!TabsDocked) { _tabsPeek = true; _leftPeek = false; }
        ApplyChrome();
    }

    // leaving a bar without moving into its panel closes the panel again after a moment
    private void EdgeBar_MouseLeave(object sender, MouseEventArgs e)
    {
        _peekClose.Stop();
        _peekClose.Start();
        ApplyChrome();
    }

    private void PeekCloseTick()
    {
        _peekClose.Stop();
        if (EdgeBars.IsMouseOver) return;
        bool changed = false;
        if (_leftPeek && !LeftPanel.IsMouseOver && !_searchMode && !LeftPanel.IsKeyboardFocusWithin) { _leftPeek = false; changed = true; }
        if (_tabsPeek && !TabsPanel.IsMouseOver) { _tabsPeek = false; changed = true; }
        if (changed) ApplyChrome();
    }

    private void LeftPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_leftPeek) return;
        if (_searchMode || LeftPanel.IsKeyboardFocusWithin) return;
        if (LeftPanel.ContextMenu?.IsOpen == true || PopupMenu.Current != null) return;
        if (EdgeBars.IsMouseOver) return;
        _leftPeek = false;
        ApplyChrome();
    }

    private void TabsPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_tabsPeek || EdgeBars.IsMouseOver) return;
        _tabsPeek = false;
        ApplyChrome();
    }

    // ============================================================= left panel views
    private void LeftView_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string view) return;
        _leftView = view;
        _s.LeftView = view;
        ShowLeftView();
    }

    private void ShowLeftView()
    {
        if (OutlineList == null) return; // during InitializeComponent
        bool s = _searchMode;
        SegHeader.Visibility = s ? Visibility.Collapsed : Visibility.Visible;
        SearchHeader.Visibility = s ? Visibility.Visible : Visibility.Collapsed;
        SearchList.Visibility = s ? Visibility.Visible : Visibility.Collapsed;
        OutlineList.Visibility = !s && _leftView == "contents" ? Visibility.Visible : Visibility.Collapsed;
        OutlineEmpty.Visibility = !s && _leftView == "contents" && _outlineRows.Count == 0 && _active != null ? Visibility.Visible : Visibility.Collapsed;
        BookmarksView.Visibility = !s && _leftView == "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        MarkupView.Visibility = !s && _leftView == "markup" ? Visibility.Visible : Visibility.Collapsed;
        if (!s && _leftView == "markup") LoadMarkup();
    }


    // ---------------------------------------------------- contents
    private void RebuildOutline()
    {
        _outlineRows.Clear();
        if (_active == null) return;
        void Add(List<OutlineItem> items, int depth)
        {
            foreach (var it in items)
            {
                var row = new OutlineRow { Item = it, Depth = depth, Expanded = _expanded.Contains(it) };
                _outlineRows.Add(row);
                if (row.Expanded) Add(it.Children, depth + 1);
            }
        }
        Add(_active.Chapters, 0);
        HighlightCurrentChapter();
        ShowLeftView();
    }

    private void HighlightCurrentChapter()
    {
        if (_active == null) return;
        var current = _active.ChapterAt(_active.CurrentPage);
        OutlineRow? best = null;
        foreach (var r in _outlineRows)
        {
            if (r.Item == current) best = r;
        }
        if (best == null && current != null)
        {
            // current section is inside a collapsed parent: highlight the nearest visible row before it
            foreach (var r in _outlineRows) if (r.Item.PageIndex >= 0 && r.Item.PageIndex <= _active.CurrentPage) best = r;
        }
        foreach (var r in _outlineRows) r.IsCurrent = r == best;
    }

    private void OutlineChevron_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not OutlineRow row) return;
        if (!_expanded.Remove(row.Item)) _expanded.Add(row.Item);
        RebuildOutline();
    }

    private async void OutlineRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutlineRow row || _active == null) return;
        LeaveHome();
        await GoToItem(row.Item);
    }

    private async Task GoToItem(OutlineItem item)
    {
        var tab = _active;
        if (tab == null) return;
        if (!string.IsNullOrEmpty(item.Uri))
        {
            try { Process.Start(new ProcessStartInfo(item.Uri) { UseShellExecute = true }); } catch { }
            return;
        }
        if (item.PageIndex < 0 || item.PageIndex >= tab.Doc.PageCount) return;
        double? y = null;
        if (item.PdfTop is double top)
        {
            var doc = tab.Doc;
            int p = item.PageIndex;
            try { y = await PdfWorker.Shared.Run(() => doc.PdfYToViewY(p, top)); } catch { }
        }
        tab.Viewer.GoToPage(item.PageIndex, y);
        tab.Viewer.FocusView();
    }

    // ---------------------------------------------------- bookmarks
    private void RefreshBookmarks()
    {
        if (_active == null) { BookmarkList.ItemsSource = null; return; }
        BookmarkList.ItemsSource = _active.Bookmarks.OrderBy(b => b.PageIndex).ThenBy(b => b.PdfTop.HasValue ? -b.PdfTop.Value : 0)
            .Select(b => new BookmarkRow { Item = b }).ToList();
        UpdateBookmarkState();
    }

    private void UpdateBookmarkState()
    {
        var t = _active;
        bool on = t != null && t.Bookmarks.Any(b => b.PageIndex == t.CurrentPage);
        AddBookmarkText.Text = t == null ? "Bookmark this page" : on ? $"Remove bookmark on page {t.LabelOf(t.CurrentPage)}" : $"Bookmark page {t.LabelOf(t.CurrentPage)}";
        PageBookmarkToggle.IsChecked = on;
        PageBookmarkIcon.FillData = on ? Icons.Bookmark : null;
        var key = Shortcuts.Text("bookmark") is string k ? $" ({k})" : "";
        PageBookmarkToggle.ToolTip = (t == null ? "Bookmark this page" : on ? $"Remove bookmark on page {t.LabelOf(t.CurrentPage)}" : $"Bookmark page {t.LabelOf(t.CurrentPage)}") + key;
    }

    private async void BookmarkToggle_Click(object sender, RoutedEventArgs e) => await ToggleBookmark();

    private async Task ToggleBookmark(int? pageIndex = null)
    {
        var tab = _active;
        if (tab == null) return;
        int p = pageIndex is int pi && pi >= 0 && pi < tab.Doc.PageCount ? pi : tab.CurrentPage;
        var existing = tab.Bookmarks.FirstOrDefault(b => b.PageIndex == p);
        if (existing != null)
        {
            tab.Bookmarks.Remove(existing);
            ShowToast($"Bookmark on page {tab.LabelOf(p)} removed");
        }
        else
        {
            var (page, viewY) = tab.Viewer.TopPosition();
            if (page != p) viewY = 0;
            var doc = tab.Doc;
            double pdfY = await PdfWorker.Shared.Run(() => doc.GetTransform(p).ViewToPdf(0, viewY).y);
            string title = tab.ChapterAt(p)?.Title is { Length: > 0 } ch ? $"{ch} · p. {tab.LabelOf(p)}" : $"Page {tab.LabelOf(p)}";
            tab.Bookmarks.Add(new OutlineItem { Title = title, PageIndex = p, PdfTop = pdfY });
            ShowToast($"Bookmarked page {tab.LabelOf(p)}");
        }
        MarkDirty(tab, bookmarks: true);
        RefreshBookmarks();
        UpdateDocChrome();
    }

    private async void BookmarkRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkRow row) { LeaveHome(); await GoToItem(row.Item); }
    }

    private void BookmarkRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkRow row) RenameBookmark(row.Item);
    }

    private void BookmarkRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkRow row) RenameBookmark(row.Item);
    }

    private void RenameBookmark(OutlineItem item)
    {
        if (_active == null) return;
        var name = Dialogs.Input(this, "Rename bookmark", item.Title, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == item.Title) return;
        item.Title = name.Trim();
        MarkDirty(_active, bookmarks: true);
        RefreshBookmarks();
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BookmarkRow row || _active == null) return;
        _active.Bookmarks.Remove(row.Item);
        MarkDirty(_active, bookmarks: true);
        RefreshBookmarks();
    }

    // ---------------------------------------------------- markup list
    private async void LoadMarkup()
    {
        var tab = _active;
        if (tab == null) { MarkupList.ItemsSource = null; return; }
        int gen = ++_markupGen;
        var doc = tab.Doc;
        var all = new List<AnnotInfo>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            int page = p;
            try { all.AddRange(await PdfWorker.Shared.Run(() => doc.GetAnnotations(page), background: true)); }
            catch { return; }
            if (gen != _markupGen || tab != _active) return;
        }
        _allMarkup = all;
        ApplyMarkupFilter();
    }

    private void ApplyMarkupFilter()
    {
        IEnumerable<AnnotInfo> items = _allMarkup;
        items = _markupFilter switch
        {
            "marks" => items.Where(a => a.Kind is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut or AnnotKind.Squiggly),
            "notes" => items.Where(a => a.Kind is AnnotKind.Note or AnnotKind.FreeText),
            "ink" => items.Where(a => a.Kind == AnnotKind.Ink),
            _ => items,
        };
        var rows = items.Select(a => new MarkupRow { Info = a }).ToList();
        MarkupList.ItemsSource = rows;
        MarkupEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkupFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string f) { _markupFilter = f; if (MarkupList != null) ApplyMarkupFilter(); }
    }

    private void MarkupRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MarkupRow row || _active == null) return;
        LeaveHome();
        _active.Viewer.GoToPage(row.Info.PageIndex, Math.Max(0, row.Info.Bounds.Y - 30));
        _active.Viewer.FocusView();
    }

    private async void MarkupDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MarkupRow row || _active == null) return;
        await _active.Viewer.DeleteAnnotation(row.Info.PageIndex, row.Info.Index);
    }

    // ---------------------------------------------------- search
    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        if (_active == null) return;
        LeaveHome();
        _searchMode = true;
        if (!LeftDocked) _leftPeek = true;
        ApplyChrome();
        ShowLeftView();
        var selected = _active.Viewer.SelectedText();
        if (!string.IsNullOrWhiteSpace(selected) && selected.Length < 80) SearchBox.Text = selected;
        Dispatcher.InvokeAsync(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
        if (SearchBox.Text.Trim().Length > 0) RunSearch();
    }

    private void CloseSearch()
    {
        _searchMode = false;
        _searchCts?.Cancel();
        _hits.Clear();
        _hitRows.Clear();
        _hitIndex = -1;
        _active?.Viewer.SetSearch(new List<SearchHit>(), null);
        ShowLeftView();
        if (_leftPeek && !LeftPanel.IsMouseOver) _leftPeek = false;
        ApplyChrome();
        _active?.Viewer.FocusView();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_searchMode) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchOption_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_searchDebounce.IsEnabled) { _searchDebounce.Stop(); RunSearch(); }
            else StepHit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        }
    }

    private async void RunSearch()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _hits.Clear();
        _hitRows.Clear();
        _hitIndex = -1;
        var tab = _active;
        string q = SearchBox.Text.Trim();
        if (tab == null || q.Length == 0) { SearchSummary.Text = ""; tab?.Viewer.SetSearch(new List<SearchHit>(), null); return; }
        tab.Viewer.SetSearch(_hits, null);
        bool matchCase = MatchCaseToggle.IsChecked == true, wholeWord = WholeWordToggle.IsChecked == true;
        var doc = tab.Doc;
        int n = doc.PageCount, start = tab.CurrentPage;
        bool jumped = false;
        SearchSummary.Text = "Searching…";
        for (int p = 0; p < n; p++)
        {
            int page = p;
            List<SearchHit> found;
            try { found = await PdfWorker.Shared.Run(() => doc.SearchPage(page, q, matchCase, wholeWord), background: true); }
            catch { return; }
            if (cts.IsCancellationRequested || tab != _active) return;
            foreach (var h in found) { _hits.Add(h); _hitRows.Add(new HitRow { Hit = h }); }
            if (!jumped && page >= start && found.Count > 0) { jumped = true; SelectHit(_hits.Count - found.Count); }
            else if (found.Count > 0) tab.Viewer.SetSearch(_hits, _hitIndex >= 0 ? _hits[_hitIndex] : null);
            if (found.Count > 0 || p % 25 == 0) SearchSummary.Text = $"Searching… {_hits.Count} found";
        }
        if (!jumped && _hits.Count > 0) SelectHit(0);
        UpdateSearchSummary();
    }

    private void UpdateSearchSummary()
    {
        SearchSummary.Text = _hits.Count == 0 ? "No results" :
            _hitIndex >= 0 ? $"{_hitIndex + 1} of {_hits.Count}" : $"{_hits.Count} results";
    }

    private void SelectHit(int i)
    {
        if (_active == null || i < 0 || i >= _hits.Count) return;
        if (_hitIndex >= 0 && _hitIndex < _hitRows.Count) _hitRows[_hitIndex].IsCurrent = false;
        _hitIndex = i;
        _hitRows[i].IsCurrent = true;
        SearchList.ScrollIntoView(_hitRows[i]);
        _active.Viewer.SetSearch(_hits, _hits[i]);
        UpdateSearchSummary();
    }

    private void StepHit(int dir)
    {
        if (_hits.Count == 0) return;
        SelectHit(((_hitIndex < 0 ? (dir > 0 ? -1 : 0) : _hitIndex) + dir + _hits.Count) % _hits.Count);
    }

    private void SearchRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HitRow row) { LeaveHome(); SelectHit(_hitRows.IndexOf(row)); }
    }

    // ============================================================= title bar actions
    private void FileMenu_Click(object sender, RoutedEventArgs e)
    {
        bool doc = _active != null;
        MenuSaveItem.IsEnabled = MenuSaveAsItem.IsEnabled = MenuPrintItem.IsEnabled = doc;
        MenuRenameItem.IsEnabled = MenuFolderItem.IsEnabled = MenuCloseItem.IsEnabled = MenuPageNumbersItem.IsEnabled = doc;
        if (!TitleMenu.IsOpen && TitleMenu.Child is FrameworkElement card)
        {
            // right edges together, so the menu stays clear of the window edge
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            TitleMenu.HorizontalOffset = FileMenuButton.ActualWidth - card.DesiredSize.Width;
        }
        TitleMenu.IsOpen = !TitleMenu.IsOpen;
    }

    // ---------------------------------------------------- page numbers (page labels)
    private void Menu_PageNumbers(object sender, RoutedEventArgs e) => ShowPageNumbers();

    /// <summary>☰ → Page numbers…: what the pages are called, e.g. when page 14 of the file is printed as 1.</summary>
    private async void ShowPageNumbers()
    {
        TitleMenu.IsOpen = false;
        var tab = _active;
        if (tab == null) return;
        if (tab.Doc.IsEncrypted)
        {
            Dialogs.Info(this, "Page numbers can't be changed", "This PDF is encrypted (protected by its maker), so Rødspætte PDF can't write page numbers into it.");
            return;
        }
        List<PageLabelRange>? current;
        try
        {
            if (tab.Labels != null) current = tab.Labels;
            else
            {
                var doc = tab.Doc;
                current = await PdfWorker.Shared.Run(() => doc.PageLabelRanges());
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("page numbers " + tab.Name, ex);
            Dialogs.Info(this, "Page numbers can't be changed", ex is NotSupportedException ? "This PDF is encrypted." : ex.Message);
            return;
        }
        var result = PageNumbersDialog.Show(this, tab.Doc.PageCount, current, tab.CurrentPage);
        if (result == null || !_tabs.Contains(tab)) return;
        tab.SetLabels(result);
        MarkDirty(tab, labels: true);
        if (tab == _active)
        {
            UpdateDocChrome();
            RebuildOutline();
            RefreshBookmarks();
            if (_leftView == "markup") LoadMarkup();
            if (_searchMode) RunSearch();
        }
        ShowToast(_s.AutoSave ? "Page numbers changed" : "Page numbers changed · save to keep them");
    }

    // ---------------------------------------------------- home page (the big logo) while files stay open
    private bool _home;

    private void SetHome(bool on)
    {
        if (_active == null) on = false;   // with nothing open the start page is showing anyway
        _home = on;
        if (_active != null)
        {
            _active.Viewer.Visibility = on ? Visibility.Hidden : Visibility.Visible;
            if (!on) _active.Viewer.ScheduleRender();
        }
        EmptyState.Visibility = on || _active == null ? Visibility.Visible : Visibility.Collapsed;
        HomeBackButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HomeToggle.IsChecked = on || _active == null;
        if (on) RefreshRecent();
        else _active?.Viewer.FocusView();
        UpdateToolbarCompact();
        _lookUp?.Reposition();
    }

    private bool _toolbarCompact;

    /// <summary>
    /// On the home page only the tool bar's first two buttons apply (back to the bottom, full screen): the rest folds away
    /// and the bar shrinks evenly towards its centre; with a document showing again it unfolds the same way.
    /// </summary>
    private void UpdateToolbarCompact()
    {
        bool compact = _home || _active == null;
        if (compact == _toolbarCompact) return;
        _toolbarCompact = compact;
        bool animate = IsLoaded && SystemParameters.ClientAreaAnimation;
        var time = TimeSpan.FromMilliseconds(animate ? 300 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ToolbarRest.IsHitTestVisible = !compact;
        if (compact)
        {
            double from = ToolbarRest.ActualWidth;
            ToolbarRest.BeginAnimation(WidthProperty, new DoubleAnimation(from, 0, time) { EasingFunction = ease });
            ToolbarRest.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(time.TotalMilliseconds * 0.6)));
            if (ToolbarRest.IsKeyboardFocusWithin) _active?.Viewer.FocusView();
        }
        else
        {
            // unfold to the width its buttons need now
            double from = ToolbarRest.ActualWidth;
            ToolbarRest.BeginAnimation(WidthProperty, null);
            ToolbarRest.Width = double.NaN;
            ToolbarRest.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double to = ToolbarRest.DesiredSize.Width;
            var grow = new DoubleAnimation(from, to, time) { EasingFunction = ease };
            grow.Completed += (_, _) =>
            {
                if (_toolbarCompact) return;
                ToolbarRest.BeginAnimation(WidthProperty, null);   // back to its natural width (the page box can grow)
                SetToolbarOffset(ToolbarShift.X, ToolbarShift.Y);   // still inside the window at full width
            };
            ToolbarRest.BeginAnimation(WidthProperty, grow);
            ToolbarRest.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(time.TotalMilliseconds * 0.7))
            {
                BeginTime = TimeSpan.FromMilliseconds(time.TotalMilliseconds * 0.3),
            });
        }
    }

    private void LeaveHome() { if (_home) SetHome(false); }
    private void HomeToggle_Click(object sender, RoutedEventArgs e) => SetHome(!_home);
    private void HomeBack_Click(object sender, RoutedEventArgs e) => SetHome(false);

    // ---------------------------------------------------- Look up (Google AI Mode)
    private LookUpPanel? _lookUp;

    private LookUpPanel EnsureLookUp()
    {
        if (_lookUp != null) return _lookUp;
        _lookUp = new LookUpPanel(_s, ViewerHost, new ToolbarDock
        {
            Outline = ToolbarOutline,
            RoomAbove = ToolbarRoomAbove,
            RoomBelow = ToolbarRoomBelow,
            Drop = ToolbarDrop,
            Inset = Toolbar.BorderThickness.Top + Toolbar.Padding.Top,
        });
        Panel.SetZIndex(_lookUp, 35);
        ViewerHost.Children.Add(_lookUp);
        _lookUp.Typing = _typing;
        _lookUp.Closed += () => _active?.Viewer.FocusView();
        _lookUp.HoverChanged += UpdateToolbarGlass;
        _lookUp.ChordPressed += c => { if (Shortcuts.Find(c)?.Id == "lookup") _lookUp.Close(); };
        return _lookUp;
    }

    private readonly Connectivity _net = new();
    private readonly TypingGlass _typing = new();

    /// <summary>Opens Look up with this text (docked above the tool bar, or beside the mouse in undocked mode).
    /// With no internet it shows "Connection Lost" instead.</summary>
    private async void OpenLookUp(string? text)
    {
        var mouse = Mouse.GetPosition(ViewerHost);
        if (!_net.IsOnline && !await _net.ConfirmOnlineAsync())
        {
            _lookUp?.CloseNow();
            if (!Splash.ConnectionLost(Root)) ShowToast("No internet connection");
            return;
        }
        EnsureLookUp().Open(text, mouse);
    }

    /// <summary>Where the bottom tool bar is in the reading area, without any room Look up opened in it (null while hidden).
    /// Worked out from the resting offset, so it is already right while the Home button's slide is still running.</summary>
    private Rect? ToolbarOutline()
    {
        if (Toolbar.Visibility != Visibility.Visible || Toolbar.ActualWidth <= 0 || _toolbarCompact) return null;
        double w = ViewerHost.ActualWidth, h = ViewerHost.ActualHeight;
        double inset = Toolbar.BorderThickness.Top + Toolbar.Padding.Top;
        double tw = Toolbar.ActualWidth, th = ToolbarButtons.ActualHeight + 2 * inset;
        double x = (w - tw) / 2 + (double)ToolbarShift.GetAnimationBaseValue(TranslateTransform.XProperty);
        double y = h - Toolbar.Margin.Bottom - th + (double)ToolbarShift.GetAnimationBaseValue(TranslateTransform.YProperty);
        return new Rect(x, y, tw, th);
    }

    /// <summary>"Tool bar transparency": the tool bar fades a little while the mouse is elsewhere. With Look up docked
    /// in it, the two are one piece: they fade together, and the mouse over either keeps both clear.</summary>
    private void UpdateToolbarGlass()
    {
        bool docked = _lookUp?.IsDocked == true;
        bool hot = !_s.ToolbarTransparency || Toolbar.IsMouseOver || _tbDrag || _typing.Active || (docked && _lookUp!.IsMouseOver);
        double target = hot ? 1.0 : LookUpPanel.GlassOpacity;
        Toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)));
        if (docked) _lookUp!.SetGlass(target);
    }
    private async void Menu_Save(object sender, RoutedEventArgs e) { TitleMenu.IsOpen = false; if (_active != null) await Save(_active, false); }
    private async void Menu_SaveAs(object sender, RoutedEventArgs e) { TitleMenu.IsOpen = false; SaveMenu.IsOpen = false; if (_active != null) await Save(_active, true); }
    private void Menu_Print(object sender, RoutedEventArgs e) { TitleMenu.IsOpen = false; PrintActive(); }
    private async void Menu_CloseTab(object sender, RoutedEventArgs e) { TitleMenu.IsOpen = false; if (_active != null) await CloseTab(_active); }
    private async void SaveButton_Click(object sender, RoutedEventArgs e) { if (_active != null) await Save(_active, false); }
    private void PrintButton_Click(object sender, RoutedEventArgs e) => PrintActive();
    private void AppearanceButton_Click(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = !AppearancePopup.IsOpen;

    private void Menu_ShowInFolder(object sender, RoutedEventArgs e)
    {
        TitleMenu.IsOpen = false;
        if (_active == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_active.FilePath}\""); } catch { }
    }

    private async void Menu_Rename(object sender, RoutedEventArgs e)
    {
        TitleMenu.IsOpen = false;
        var tab = _active;
        if (tab == null) return;
        var current = System.IO.Path.GetFileNameWithoutExtension(tab.FilePath);
        var name = Dialogs.Input(this, "Rename file", current, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == current) return;
        name = name.Trim();
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { Dialogs.Info(this, "That name can't be used", "File names can't contain \\ / : * ? \" < > |"); return; }
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tab.FilePath)!, name + System.IO.Path.GetExtension(tab.FilePath));
        if (File.Exists(target)) { Dialogs.Info(this, "A file with that name already exists", target); return; }
        try
        {
            await Task.Run(() => File.Move(tab.FilePath, target));
            tab.ReplaceDocument(tab.Doc, target);
            _taskbar.Rename(tab);
            UpdateDocChrome();
            RefreshTabs();
        }
        catch (Exception ex) { Dialogs.Info(this, "Could not rename the file", ex.Message); }
    }

    private void PrintActive()
    {
        if (_active == null) return;
        new PrintSheet(this, _active.Doc, _active.CurrentPage, _active.Name).ShowDialog();
    }

    // ============================================================= autosave
    private readonly Dictionary<DocumentTab, DispatcherTimer> _autoSaveTimers = new();
    private readonly HashSet<DocumentTab> _saving = new();

    private void MarkDirty(DocumentTab tab, bool bookmarks = false, bool labels = false)
    {
        tab.EditVersion++;
        if (bookmarks) tab.BookmarksDirty = true;
        if (labels) tab.LabelsDirty = true;
        tab.Dirty = true;
        if (_s.AutoSave) ScheduleAutoSave(tab);
    }

    /// <summary>Saves shortly after the last change, so a burst (a pen stroke, a few quick highlights) is one save.</summary>
    private void ScheduleAutoSave(DocumentTab tab)
    {
        if (!_autoSaveTimers.TryGetValue(tab, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (!_tabs.Contains(tab) || !_s.AutoSave || !(tab.Dirty || tab.BookmarksDirty)) return;
                if (_saving.Contains(tab)) { timer.Start(); return; }
                await Save(tab, false, quiet: true);
            };
            _autoSaveTimers[tab] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    private void SaveMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AutoSaveSwitch.IsChecked = _s.AutoSave;
        SaveMenu.IsOpen = !SaveMenu.IsOpen;
    }

    private void AutoSave_Click(object sender, RoutedEventArgs e) => SetAutoSave(AutoSaveSwitch.IsChecked == true);

    /// <summary>Autosave (Save options): remembered, so it is the default next time.</summary>
    private void SetAutoSave(bool on)
    {
        _s.AutoSave = on;
        AutoSaveSwitch.IsChecked = on;
        _s.Save();
        UpdateSaveButton();
        if (_s.AutoSave) foreach (var t in _tabs.Where(t => t.Dirty || t.BookmarksDirty)) ScheduleAutoSave(t);
        ShowToast(_s.AutoSave ? "Autosave on" : "Autosave off");
    }

    private void UpdateSaveButton() =>
        SaveButton.Content = _s.AutoSave ? "Autosave" : "Save";

    private Task<bool> Save(DocumentTab tab, bool saveAs) => Save(tab, saveAs, quiet: false);

    /// <summary>
    /// Writes the document. Quiet saves (autosave) show no wait cursor or dialogs, and keep the open document
    /// as it is unless bookmarks had to be written into the file.
    /// </summary>
    private async Task<bool> Save(DocumentTab tab, bool saveAs, bool quiet)
    {
        string path = tab.FilePath;
        if (saveAs)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PDF document (*.pdf)|*.pdf",
                FileName = tab.Name,
                InitialDirectory = System.IO.Path.GetDirectoryName(tab.FilePath),
                AddExtension = true,
                DefaultExt = ".pdf",
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        else if (!tab.Dirty && !tab.BookmarksDirty) return true;

        int version = tab.EditVersion;
        _saving.Add(tab);
        try
        {
            if (!quiet) Mouse.OverrideCursor = Cursors.Wait;
            tab.Viewer.CancelEditor();
            var doc = tab.Doc;
            var roots = tab.BookmarksDirty || tab.OutlinePending ? OutlineWriter.Combine(tab.Chapters, tab.Bookmarks) : null;
            bool writeLabels = tab.LabelsDirty || tab.LabelsPending;
            var labels = tab.Labels;
            int pageCount = doc.PageCount;
            byte[] bytes = await PdfWorker.Shared.Run(() => doc.SaveToBytes());
            if (roots != null)
            {
                try { bytes = await Task.Run(() => OutlineWriter.Apply(bytes, roots)); }
                catch (Exception ex)
                {
                    ErrorLog.Write("bookmarks " + System.IO.Path.GetFileName(path), ex);
                    if (quiet) ShowToast("Bookmarks were not saved: " + ex.Message);
                    else Dialogs.Info(this, "Bookmarks were not saved", ex.Message);
                }
            }
            if (writeLabels)
            {
                try { bytes = await Task.Run(() => PageNumbering.Write(bytes, labels, pageCount)); }
                catch (Exception ex)
                {
                    ErrorLog.Write("page numbers " + System.IO.Path.GetFileName(path), ex);
                    if (quiet) ShowToast("Page numbers were not saved: " + ex.Message);
                    else Dialogs.Info(this, "Page numbers were not saved", ex.Message);
                }
            }
            await Task.Run(() =>
            {
                var tmp = path + ".rodspaette-tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
            });
            // autosave never swaps the open document, so an edit made while it writes can't be lost
            bool reopen = !quiet;
            if (reopen)
            {
                var pw = doc.Password;
                var fresh = await PdfWorker.Shared.Run(() => PdfDocument.Open(bytes, pw));
                tab.ReplaceDocument(fresh, path);
                await OcrService.Reattach(tab);
                try { await PdfWorker.Shared.Run(() => doc.Dispose()); } catch { }
            }
            // anything edited while this save was writing still needs saving
            bool editedMeanwhile = tab.EditVersion != version;
            tab.OutlinePending = !reopen && roots != null;
            tab.LabelsPending = !reopen && writeLabels;
            tab.Dirty = editedMeanwhile;
            if (!editedMeanwhile) tab.BookmarksDirty = false;
            if (!editedMeanwhile) tab.LabelsDirty = false;
            if (reopen && !tab.LabelsDirty)
            {
                var fresh = tab.Doc;
                try { tab.LoadedLabels(await PdfWorker.Shared.Run(() => fresh.PageLabels())); } catch { }
            }
            if (reopen) tab.Viewer.Reload();
            if (editedMeanwhile && _s.AutoSave) ScheduleAutoSave(tab);
            _taskbar.Rename(tab);
            _taskbar.Invalidate(tab);
            _s.Touch(path, tab.CurrentPage);
            _s.Save();
            if (tab == _active)
            {
                UpdateDocChrome();
                if (_leftView == "markup") LoadMarkup();
                RefreshTabs();
            }
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write((quiet ? "autosave " : "save ") + System.IO.Path.GetFileName(path), ex);
            if (quiet) ShowToast("Autosave failed: " + ex.Message);
            else Dialogs.Info(this, "Could not save", ex.Message);
            return false;
        }
        finally
        {
            _saving.Remove(tab);
            if (!quiet) Mouse.OverrideCursor = null;
        }
    }

    // ============================================================= appearance
    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string t || !IsLoaded) return;
        var theme = Enum.Parse<AppTheme>(t);
        if (theme == Theme.Current) return;
        Theme.Apply(theme);
        _s.Theme = theme;
        _s.Save();
        Win32.SetDwmBool(new WindowInteropHelper(this).Handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, theme == AppTheme.Dark);
        _lookUp?.ApplyTheme();
        AfterThemeChange();
    }

    private void TintToggle_Click(object sender, RoutedEventArgs e)
    {
        Theme.TintPages = TintToggle.IsChecked == true;
        _s.TintPages = Theme.TintPages;
        _s.Save();
        AfterThemeChange();
    }

    private void AfterThemeChange()
    {
        foreach (var t in _tabs) { t.Viewer.InvalidateAll(); _taskbar.Invalidate(t); }
        RebuildOutline();
        RefreshTabs();
        foreach (var r in _hitRows) { bool c = r.IsCurrent; r.IsCurrent = !c; r.IsCurrent = c; }
        ApplyChrome();
    }

    // ============================================================= tools
    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string t) return;
        _tool = Enum.Parse<Tool>(t);
        if (_active != null) _active.Viewer.Tool = _tool;
    }

    private void Tool_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string t) return;
        _colorKey = ColorKey(Enum.Parse<Tool>(t));
        ColorPopup.PlacementTarget = rb;
        ColorPopup.IsOpen = true;
        e.Handled = true;
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string hex) return;
        ColorPopup.IsOpen = false;
        SetToolColor(_colorKey, hex);
    }

    private static string ColorKey(Tool t) => t switch
    {
        Tool.Highlight => "highlight", Tool.Underline => "underline", Tool.Strike => "strike",
        Tool.Pen => "pen", Tool.Note => "note", _ => "text",
    };

    private void SetToolColor(string key, string hex)
    {
        _s.ToolColors[key] = hex;
        if (key == "highlight") HighlightSwatch.Fill = BrushFrom(hex);
        _s.Save();
    }

    private static readonly (Tool tool, string icon, string tip)[] ToolButtons =
    {
        (Tool.Select, Icons.ToolSelect, "Select text"),
        (Tool.Highlight, Icons.ToolHighlight, "Highlight"),
        (Tool.Underline, Icons.ToolUnderline, "Underline"),
        (Tool.Strike, Icons.ToolStrike, "Strikethrough"),
        (Tool.Pen, Icons.ToolPen, "Freehand pen"),
        (Tool.Note, Icons.ToolNote, "Note"),
        (Tool.Text, Icons.ToolText, "Text box"),
    };

    private void SelectTool(Tool t)
    {
        RadioButton rb = t switch
        {
            Tool.Highlight => ToolHighlight, Tool.Underline => ToolUnderline, Tool.Strike => ToolStrike,
            Tool.Pen => ToolPen, Tool.Note => ToolNote, Tool.Text => ToolText, _ => ToolSelect,
        };
        rb.IsChecked = true;
    }

    /// <summary>Rows shared by every right-click menu on the page: tools, the colour for the current tool, undo, look up,
    /// bookmark, save, print and update request. (Update app lives in the Settings dropdown.)</summary>
    private void AddMenuExtras(PopupMenu m)
    {
        var tools = new List<(string icon, string tip, Action action)>();
        foreach (var b in ToolButtons) { var t = b.tool; tools.Add((b.icon, b.tip, () => SelectTool(t))); }
        m.IconRow(tools, Array.FindIndex(ToolButtons, b => b.tool == _tool));

        var colourFor = _tool == Tool.Select ? Tool.Highlight : _tool;
        var key = ColorKey(colourFor);
        var info = ToolButtons.First(b => b.tool == colourFor);
        var current = _s.ToolColors.TryGetValue(key, out var c) ? c : Palette[0];
        m.SwatchRow(info.icon, info.tip + " colour", Palette, current, hex => SetToolColor(key, hex));
        m.Separator();

        var tab = _active;
        m.Item("Undo", Shortcuts.Text("undo"), async () => { if (tab != null && !await tab.Viewer.Undo()) ShowToast("Nothing to undo"); },
               Icons.Undo, enabled: tab != null && tab.UndoStack.Count > 0);
        if (!m.Has("lookup")) m.Item("Look up…", Shortcuts.Text("lookup"), LookUp, Icons.LookUp);
        if (tab != null)
        {
            int p = tab.Viewer.MenuPage >= 0 ? tab.Viewer.MenuPage : tab.CurrentPage;
            bool marked = tab.Bookmarks.Any(b => b.PageIndex == p);
            m.Item(marked ? $"Remove bookmark on page {tab.LabelOf(p)}" : $"Bookmark page {tab.LabelOf(p)}", Shortcuts.Text("bookmark"), async () => await ToggleBookmark(p), Icons.Bookmark);
        }
        m.Separator();
        m.Item("Save", Shortcuts.Text("save"), async () => { if (tab != null) await Save(tab, false); }, Icons.Save, enabled: tab != null);
        m.Item("Save As…", Shortcuts.Text("saveas"), async () => { if (tab != null) await Save(tab, true); }, Icons.SaveAs, enabled: tab != null);
        m.Item("Print…", Shortcuts.Text("print"), PrintActive, Icons.Print, enabled: tab != null);
        m.Separator();
        m.Item("Update request…", null, ReportError, Icons.Report);
    }

    private void ReportError()
    {
        var dir = ReportDialog.Show(this, DescribeState());
        if (dir != null) ShowToast("Request saved");
    }

    /// <summary>What the app was doing, attached to error reports.</summary>
    private string DescribeState()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"- App version: {AppInfo.Version}");
        sb.AppendLine($"- Windows: {Environment.OSVersion.VersionString}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        sb.AppendLine($"- Display scale: {System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100:0}%");
        sb.AppendLine($"- Theme: {Theme.Current}, tint pages: {_s.TintPages}");
        sb.AppendLine($"- Window: {WindowState}, {ActualWidth:0}×{ActualHeight:0}");
        sb.AppendLine($"- Open tabs: {_tabs.Count}");
        if (_active is { } t)
        {
            sb.AppendLine($"- Active file: {t.FilePath}");
            sb.AppendLine($"- Page: {t.CurrentPage + 1} of {t.Doc.PageCount}, zoom {t.Viewer.Zoom * 100:0}%");
            sb.AppendLine($"- Tool: {_tool}, unsaved changes: {t.Dirty || t.BookmarksDirty}");
            sb.AppendLine($"- OCR: {(string.IsNullOrEmpty(t.OcrStatus) ? "idle" : t.OcrStatus)}");
        }
        else sb.AppendLine("- No file open");
        return sb.ToString();
    }

    private static Brush BrushFrom(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null && !await _active.Viewer.Undo()) ShowToast("Nothing to undo");
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _active?.Viewer.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _active?.Viewer.ZoomOut();
    private void ZoomLabel_Click(object sender, RoutedEventArgs e) => _active?.Viewer.FitToWidth();

    private void PageBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _active != null)
        {
            int p = _active.FindPage(PageBox.Text);
            if (p >= 0) _active.Viewer.GoToPage(p);
            else ShowToast($"No page \u201C{PageBox.Text.Trim()}\u201D");
            _active.Viewer.FocusView();
            ShowPageNumber(_active);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { _active?.Viewer.FocusView(); e.Handled = true; }
    }

    // ============================================================= keyboard
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool inText = Keyboard.FocusedElement is TextBox or PasswordBox;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var v = _active?.Viewer;

        if (_lookUp != null && _lookUp.IsOpen)
        {
            bool own = !KeyChord.IsModifier(key) && Shortcuts.Find(new KeyChord(Keyboard.Modifiers, KeyChord.Normalize(key)))?.Id == "lookup";
            if (key == Key.Escape || own) { e.Handled = true; _lookUp.Close(); return; }
            if (_lookUp.IsKeyboardFocusWithin) { _typing.Typed(); return; }   // typing in the Google page
        }
        if (Toolbar.IsKeyboardFocusWithin && !KeyChord.IsModifier(key)) _typing.Typed();   // the page box

        // Settings → Shortcuts… (in a text box only keys with Ctrl or Alt, or F-keys, and never the page-only ones)
        if (!KeyChord.IsModifier(key) && key != Key.Escape)
        {
            var chord = new KeyChord(Keyboard.Modifiers, KeyChord.Normalize(key));
            if (Shortcuts.Find(chord) is { } cmd && (!inText || (!cmd.PageOnly && chord.WorksInText)) && RunShortcut(cmd.Id, inText))
            {
                e.Handled = true;
                return;
            }
        }

        if (key == Key.Escape && PopupMenu.Current != null) { e.Handled = true; PopupMenu.Current.Close(); return; }
        if (key == Key.Escape && _radial?.IsOpen == true) { e.Handled = true; CloseRadial(); return; }
        if (key == Key.Escape && _home) { e.Handled = true; SetHome(false); return; }
        if (inText) return;
        if (key == Key.Escape)
        {
            e.Handled = true;
            if (v?.HasEditor == true) { v.CancelEditor(); return; }
            if (_searchMode) { CloseSearch(); return; }
            if (_tool != Tool.Select) { ToolSelect.IsChecked = true; return; }
            if (_wantFullScreen) { ToggleFullScreen(false); return; }
            v?.ClearSelection();
        }
    }

    /// <summary>Runs a command from Settings → Shortcuts…. False when it doesn't apply right now, so the key
    /// does what it normally does (Ctrl+C with nothing selected, Delete with no markup selected…).</summary>
    private bool RunShortcut(string id, bool inText)
    {
        var tab = _active;
        switch (id)
        {
            case "open": ShowOpenDialog(); return true;
            case "save": if (tab != null) _ = Save(tab, false); return true;
            case "saveas": if (tab != null) _ = Save(tab, true); return true;
            case "print": PrintActive(); return true;
            case "closetab": if (tab != null) _ = CloseTab(tab); return true;
            case "nexttab": CycleTabs(1); return true;
            case "prevtab": CycleTabs(-1); return true;
            case "lasttab": SwitchToLastTab(); return true;
            case "pagenumbers": ShowPageNumbers(); return true;
            case "search": if (_searchMode) CloseSearch(); else OpenSearch(); return true;
            case "nexthit": if (!_searchMode) OpenSearch(); else StepHit(1); return true;
            case "prevhit": if (!_searchMode) OpenSearch(); else StepHit(-1); return true;
            case "lookup": if (_lookUp?.IsOpen == true) _lookUp.Close(); else LookUp(); return true;
            case "bookmark": _ = ToggleBookmark(); return true;
            case "panels": TogglePanels(); return true;
            case "fullscreen": ToggleFullScreen(!_wantFullScreen); return true;
            case "home": SetHome(!_home); return true;
        }
        if (inText) return false;
        switch (id)
        {
            case "tool-select": SelectTool(Tool.Select); return true;
            case "tool-highlight": MarkupOrTool(Tool.Highlight); return true;
            case "tool-underline": MarkupOrTool(Tool.Underline); return true;
            case "tool-strike": MarkupOrTool(Tool.Strike); return true;
            case "tool-pen": SelectTool(Tool.Pen); return true;
            case "tool-note": SelectTool(Tool.Note); return true;
            case "tool-text": SelectTool(Tool.Text); return true;
        }
        var v = tab?.Viewer;
        if (v == null) return false;
        switch (id)
        {
            case "undo": _ = UndoIn(v); return true;
            case "copy": return v.CopySelection();
            case "zoomin": v.ZoomIn(); return true;
            case "zoomout": v.ZoomOut(); return true;
            case "fitwidth": v.FitToWidth(); return true;
            case "delete":
                if (!v.HasSelectedAnnotation) return false;
                _ = v.DeleteSelectedAnnotation();
                return true;
        }
        return false;
    }

    private async Task UndoIn(PdfViewer v)
    {
        if (!await v.Undo()) ShowToast("Nothing to undo");
    }

    // ============================================================= closing
    /// <summary>Asks about each tab with unsaved changes. False if the user cancelled or a save failed.</summary>
    private async Task<bool> ConfirmUnsaved()
    {
        foreach (var t in _tabs.Where(t => t.Dirty || t.BookmarksDirty).ToList())
        {
            Activate(t);
            int r = Dialogs.Ask(this, $"Save changes to {t.Name}?", "", "Save", "Don't save", "Cancel");
            if (r == 0) { if (!await Save(t, false)) return false; }
            else if (r != 1) return false;
        }
        return true;
    }

    // ============================================================= update app
    private bool _updating;

    private async void UpdateApp()
    {
        if (_updating) { ShowToast("Already updating…", sticky: true); return; }
        var source = Updater.FindSource(_s);
        if (source == null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Rødspætte PDF source folder (the one with build.cmd)" };
            if (dlg.ShowDialog(this) != true) return;
            source = Updater.Normalize(dlg.FolderName);
            if (source == null) { Dialogs.Info(this, "That isn't the source folder", "Choose the folder that contains build.cmd."); return; }
        }
        if (_s.SourceFolder != source) { _s.SourceFolder = source; _s.Save(); }

        _updating = true;
        ShowToast("Building update…", sticky: true);
        var outDir = Updater.OutputDir(source);
        var (ok, output) = await Updater.Build(source, outDir);
        _updating = false;
        HideToast();

        if (!ok)
        {
            ErrorLog.Write("update build", new Exception(output));
            Dialogs.Info(this, "Update failed", Updater.Summarize(output));
            return;
        }
        if (Updater.SameAsInstalled(outDir)) { ShowToast("Already up to date"); return; }

        if (Dialogs.Ask(this, "Update ready", "Rødspætte PDF will restart and reopen your files.", "Restart now", "Later") != 0) return;
        if (!await ConfirmUnsaved()) return;
        var reopen = _tabs.Where(t => t != _active).Append(_active).Where(t => t != null).Select(t => t!.FilePath).ToList();
        try { Updater.LaunchSwap(source, outDir, reopen); }
        catch (Exception ex) { ErrorLog.Write("update swap", ex); Dialogs.Info(this, "Update failed", ex.Message); return; }
        _closeConfirmed = true;
        Close();
    }

    private void SettingsUpdate_Click(object sender, RoutedEventArgs e) { SettingsMenu.IsOpen = false; UpdateApp(); }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed && _tabs.Any(t => t.Dirty || t.BookmarksDirty))
        {
            e.Cancel = true;
            if (!await ConfirmUnsaved()) return;
            _closeConfirmed = true;
            // close again after this Closing event has finished; calling Close() inside it throws
            _ = Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
            return;
        }
        if (_fullScreen) { _wantFullScreen = false; SetFullScreen(false); }
        _s.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty) { _s.WinLeft = b.Left; _s.WinTop = b.Top; _s.WinWidth = b.Width; _s.WinHeight = b.Height; }
        foreach (var t in _tabs) { _s.Touch(t.FilePath, t.CurrentPage); t.OcrCancel.Cancel(); }
        if (_s.RestoreSession)
        {
            _s.LeftPanelVisible = _leftVisible;
            _s.TabsPinned = _tabsPinned;
        }
        _s.SessionFiles = _tabs.Select(t => t.FilePath).ToList();
        _s.SessionActive = _active?.FilePath;
        _s.Save();
        _taskbar.Dispose();
    }
}
