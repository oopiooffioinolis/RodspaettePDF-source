using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rodspaette.Core;

namespace Rodspaette.App;

public enum Tool { Select, Highlight, Underline, Strike, Pen, Note, Text }

/// <summary>
/// Continuous-scroll page view. Only pages near the viewport exist as elements.
/// Each page shows a low-resolution base bitmap plus a sharp "detail" bitmap of the visible region,
/// so zooming in stays crisp without rendering whole pages at huge sizes.
/// </summary>
public sealed class PdfViewer : Grid
{
    private const double Gap = 14, SideMargin = 24, TopMargin = 18, BottomSpace = 96;
    private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6.4, 8 };

    private readonly DocumentTab _tab;
    private readonly AppSettings _settings;
    private readonly ScrollViewer _sv;
    private readonly Canvas _canvas;
    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<int, PageElement> _live = new();
    private double[] _top = Array.Empty<double>(), _w = Array.Empty<double>(), _h = Array.Empty<double>();
    private double _k = 96.0 / 72.0;     // DIPs per point
    private double _contentWidth;
    private int _globalRev;
    private bool _layoutDone;

    private Tool _tool = Tool.Select;
    private int _selPage = -1, _selA, _selB;
    private PageText? _selText;
    private bool _dragging, _moved;
    private List<ViewPoint>? _ink;
    private Polyline? _inkLine;
    private PageElement? _inkPage;
    private AnnotInfo? _selectedAnnot;
    private List<SearchHit> _hits = new();
    private SearchHit? _currentHit;
    private FrameworkElement? _editor;

    public event Action<int>? PageChanged;
    public event Action? ZoomChanged;
    public event Action<int>? Edited;
    public event Action<string>? Hint;

    public bool FitWidth { get; private set; }
    public double Zoom => _k / (96.0 / 72.0);
    public int CurrentPage { get; private set; }
    public bool HasSelection => _selPage >= 0 && _selText != null && _moved;

    public Tool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            CancelEditor();
            ClearSelection();
            _canvas.Cursor = value switch
            {
                Tool.Select => Cursors.IBeam,
                Tool.Pen => Cursors.Pen,
                Tool.Note or Tool.Text => Cursors.Cross,
                _ => Cursors.IBeam,
            };
            foreach (var el in _live.Values) el.Cursor = null;
        }
    }

    public PdfViewer(DocumentTab tab, AppSettings settings)
    {
        _tab = tab;
        _settings = settings;
        FitWidth = settings.FitWidth;
        _k = settings.Zoom * 96.0 / 72.0;
        _canvas = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.IBeam };
        _canvas.MouseRightButtonUp += Canvas_MouseRightButtonUp;
        _sv = new ScrollViewer
        {
            Content = _canvas,
            CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = true,
            FocusVisualStyle = null,
            PanningMode = PanningMode.Both,   // one-finger drag on touch screens, both directions
        };
        _sv.SetResourceReference(Control.BackgroundProperty, "B.Viewer");
        Children.Add(_sv);
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(35) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); UpdateVisible(); };
        _sv.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0) { Relayout(); return; }
            UpdateCurrentPage();
            ScheduleRender();
        };
        _sv.PreviewMouseWheel += OnWheel;
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowHoverCard(); };
        SizeChanged += (_, _) => { if (!_layoutDone) { Relayout(); _layoutDone = true; } };
        Loaded += (_, _) => { Relayout(); ScheduleRender(); };
    }

    // ================================================================= layout
    private PdfDocument Doc => _tab.Doc;

    private double ViewportWidth => _sv.ViewportWidth > 0 ? _sv.ViewportWidth : Math.Max(200, ActualWidth - 12);

    private void ComputeLayout()
    {
        int n = Doc.PageCount;
        _top = new double[n]; _w = new double[n]; _h = new double[n];
        double maxW = 1;
        for (int i = 0; i < n; i++) maxW = Math.Max(maxW, Doc.GetPageSize(i).Width);
        double avail = ViewportWidth;
        if (FitWidth) _k = Math.Clamp((avail - 2 * SideMargin) / maxW, 0.08, 12);
        double y = TopMargin;
        for (int i = 0; i < n; i++)
        {
            var s = Doc.GetPageSize(i);
            _w[i] = s.Width * _k; _h[i] = s.Height * _k;
            _top[i] = y;
            y += _h[i] + Gap;
        }
        _contentWidth = Math.Max(avail, maxW * _k + 2 * SideMargin);
        _canvas.Width = _contentWidth;
        _canvas.Height = y - Gap + BottomSpace;
    }

    private (int page, double frac) Anchor(double viewportY)
    {
        if (_top.Length == 0) return (0, 0);
        double y = _sv.VerticalOffset + viewportY;
        int p = PageAt(y);
        double frac = _h[p] > 0 ? (y - _top[p]) / _h[p] : 0;
        return (p, frac);
    }

    private void Relayout(double anchorY = -1)
    {
        if (Doc.PageCount == 0) return;
        double ay = anchorY >= 0 ? anchorY : 0;
        bool hadLayout = _top.Length == Doc.PageCount && _top.Length > 0;
        (int page, double frac) anchor = hadLayout ? Anchor(ay) : (CurrentPage, 0.0);
        double hFrac = _contentWidth > 0 ? (_sv.HorizontalOffset + _sv.ViewportWidth / 2) / _contentWidth : 0.5;
        ComputeLayout();
        foreach (var el in _live.Values) PlaceElement(el);
        _sv.UpdateLayout();
        _sv.ScrollToVerticalOffset(_top[anchor.page] + anchor.frac * _h[anchor.page] - ay);
        _sv.ScrollToHorizontalOffset(Math.Max(0, hFrac * _contentWidth - _sv.ViewportWidth / 2));
        ZoomChanged?.Invoke();
        ScheduleRender();
    }

    private int PageAt(double y)
    {
        int lo = 0, hi = _top.Length - 1;
        if (hi < 0) return 0;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_top[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private void UpdateCurrentPage()
    {
        if (_top.Length == 0) return;
        int p = PageAt(_sv.VerticalOffset + _sv.ViewportHeight * 0.3);
        if (p != CurrentPage)
        {
            CurrentPage = p;
            _tab.CurrentPage = p;
            PageChanged?.Invoke(p);
        }
    }

    public void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    // ================================================================= page elements
    private sealed class PageElement : Grid
    {
        public int Index;
        public readonly Border Paper = new() { BorderThickness = new Thickness(1) };
        public readonly Image Base = new() { Stretch = Stretch.Fill };
        public readonly Canvas DetailLayer = new() { ClipToBounds = true };
        public readonly Image Detail = new() { Stretch = Stretch.Fill };
        public readonly Canvas Overlay = new() { IsHitTestVisible = false };
        public readonly Canvas Editors = new();
        public double BaseScale;
        public int BaseRev = -1, BaseGen, DetailGen, DetailRev = -1;
        public Rect DetailRect = Rect.Empty;
        public double DetailScale;
        public bool Live = true;
        public PageText? Text;
        public List<AnnotInfo>? Annots;
        public int AnnotGen;                                        // bumped whenever the page's annotations change
        public bool MarkersLoading;
        public readonly Canvas MarkerLayer = new() { IsHitTestVisible = false };
        public readonly List<(AnnotInfo Annot, Rect Hit)> MarkerHits = new();   // comment bubbles, element pixels

        public PageElement()
        {
            Background = Brushes.Transparent;
            RenderOptions.SetBitmapScalingMode(Base, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(Detail, BitmapScalingMode.HighQuality);
            Paper.SetResourceReference(Border.BorderBrushProperty, "B.PageShadow");
            DetailLayer.Children.Add(Detail);
            Children.Add(Paper);
            Children.Add(Base);
            Children.Add(DetailLayer);
            Children.Add(Overlay);
            Children.Add(MarkerLayer);
            Children.Add(Editors);
        }
    }

    private PageElement GetOrCreate(int i)
    {
        if (_live.TryGetValue(i, out var el)) return el;
        el = new PageElement { Index = i };
        el.Paper.Background = new SolidColorBrush(Theme.PaperColor(Theme.Current, Theme.TintPages));
        el.MouseLeftButtonDown += Page_MouseLeftButtonDown;
        el.MouseMove += Page_MouseMove;
        el.MouseLeftButtonUp += Page_MouseLeftButtonUp;
        el.MouseRightButtonUp += Page_MouseRightButtonUp;
        el.MouseLeave += (_, _) => { if (_hoverEl == el) HideHover(); };
        _live[i] = el;
        _canvas.Children.Add(el);
        PlaceElement(el);
        return el;
    }

    private void PlaceElement(PageElement el)
    {
        int i = el.Index;
        el.Width = _w[i];
        el.Height = _h[i];
        Canvas.SetLeft(el, (_contentWidth - _w[i]) / 2);
        Canvas.SetTop(el, _top[i]);
        el.DetailRect = Rect.Empty;
        el.Detail.Source = null;
        RedrawOverlay(el);
        DrawMarkers(el);
    }

    private void DropPage(PageElement el)
    {
        if (_hoverEl == el) HideHover();
        el.Live = false;
        el.BaseGen++; el.DetailGen++;
        el.Base.Source = null; el.Detail.Source = null;
        if (_editor != null && el.Editors.Children.Contains(_editor)) CancelEditor();
        _canvas.Children.Remove(el);
        _live.Remove(el.Index);
    }

    // ================================================================= rendering
    private void UpdateVisible()
    {
        if (_top.Length == 0 || !IsVisible) return;
        double vt = _sv.VerticalOffset, vh = _sv.ViewportHeight, vb = vt + vh;
        int first = PageAt(Math.Max(0, vt - vh * 0.75));
        int last = PageAt(vb + vh * 0.75);
        foreach (var el in _live.Values.ToList())
            if (el.Index < first || el.Index > last) DropPage(el);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = first; i <= last; i++)
        {
            var el = GetOrCreate(i);
            RequestBase(el, dpi);
            RequestDetail(el, dpi, vt, vb);
        }
    }

    private int Rev(int i) => Doc.PageRevision[i] + _globalRev * 100000;

    private static BitmapSource MakeBitmap(byte[] px, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private async void RequestBase(PageElement el, double dpi)
    {
        int i = el.Index;
        var size = Doc.GetPageSize(i);
        double needed = _k * dpi;
        double scale = Math.Min(needed, 1700.0 / Math.Max(size.Width, size.Height));
        int rev = Rev(i);
        if (el.BaseRev == rev && Math.Abs(el.BaseScale - scale) <= scale * 0.02) return;
        int gen = ++el.BaseGen;
        int w = Math.Max(1, (int)Math.Round(size.Width * scale)), h = Math.Max(1, (int)Math.Round(size.Height * scale));
        var doc = Doc;
        var theme = Theme.Current;
        bool tint = Theme.TintPages;
        try
        {
            var px = await PdfWorker.Shared.Run(() =>
            {
                if (gen != el.BaseGen || !el.Live) return null;
                var b = doc.Render(i, scale, 0, 0, w, h);
                Theme.TintPixels(b, theme, tint);
                return b;
            });
            if (px == null || gen != el.BaseGen || !el.Live) return;
            el.Base.Source = MakeBitmap(px, w, h);
            el.BaseScale = scale;
            el.BaseRev = rev;
            if (el.DetailRev != rev) { el.DetailRect = Rect.Empty; el.Detail.Source = null; ScheduleRender(); }
        }
        catch { /* document closed or replaced while rendering */ }
    }

    private async void RequestDetail(PageElement el, double dpi, double vt, double vb)
    {
        int i = el.Index;
        var size = Doc.GetPageSize(i);
        double needed = _k * dpi;
        double baseScale = Math.Min(needed, 1700.0 / Math.Max(size.Width, size.Height));
        if (needed <= baseScale * 1.05) { el.Detail.Source = null; el.DetailRect = Rect.Empty; return; }

        double left = (_contentWidth - _w[i]) / 2;
        var vis = new Rect(_sv.HorizontalOffset - left, vt - _top[i], _sv.ViewportWidth, vb - vt);
        vis.Intersect(new Rect(0, 0, _w[i], _h[i]));
        if (vis.IsEmpty || vis.Width < 1 || vis.Height < 1) return;
        int rev = Rev(i);
        if (el.DetailRev == rev && Math.Abs(el.DetailScale - needed) < 1e-6 && !el.DetailRect.IsEmpty && el.DetailRect.Contains(vis)) return;

        var want = vis;
        want.Inflate(vis.Width * 0.15, vis.Height * 0.25);
        want.Intersect(new Rect(0, 0, _w[i], _h[i]));
        int rx = (int)Math.Floor(want.X * dpi), ry = (int)Math.Floor(want.Y * dpi);
        int rw = (int)Math.Ceiling(want.Width * dpi), rh = (int)Math.Ceiling(want.Height * dpi);
        if (rw * (long)rh > 40_000_000) return;
        int gen = ++el.DetailGen;
        var doc = Doc;
        var theme = Theme.Current;
        bool tint = Theme.TintPages;
        try
        {
            var px = await PdfWorker.Shared.Run(() =>
            {
                if (gen != el.DetailGen || !el.Live) return null;
                var b = doc.Render(i, needed, rx, ry, rw, rh);
                Theme.TintPixels(b, theme, tint);
                return b;
            });
            if (px == null || gen != el.DetailGen || !el.Live) return;
            el.Detail.Source = MakeBitmap(px, rw, rh);
            el.Detail.Width = rw / dpi;
            el.Detail.Height = rh / dpi;
            Canvas.SetLeft(el.Detail, rx / dpi);
            Canvas.SetTop(el.Detail, ry / dpi);
            el.DetailRect = new Rect(rx / dpi, ry / dpi, rw / dpi, rh / dpi);
            el.DetailScale = needed;
            el.DetailRev = rev;
        }
        catch { }
    }

    /// <summary>Theme or tint changed: re-render everything visible.</summary>
    public void InvalidateAll()
    {
        _globalRev++;
        var paper = new SolidColorBrush(Theme.PaperColor(Theme.Current, Theme.TintPages));
        foreach (var el in _live.Values) { el.Paper.Background = paper; RedrawOverlay(el); }
        ScheduleRender();
    }

    /// <summary>The document was reloaded (after save): rebuild everything at the same position.</summary>
    public void Reload()
    {
        var keep = Anchor(0);
        foreach (var el in _live.Values.ToList()) DropPage(el);
        _selectedAnnot = null;
        ClearSelection();
        ComputeLayout();
        _sv.UpdateLayout();
        if (keep.page < _top.Length) _sv.ScrollToVerticalOffset(_top[keep.page] + keep.frac * _h[keep.page]);
        ScheduleRender();
    }

    public void RefreshPage(int page)
    {
        if (_live.TryGetValue(page, out var el)) { el.Text = null; ReloadMarkers(el); RedrawOverlay(el); }
        ScheduleRender();
    }

    // ================================================================= navigation & zoom
    public void GoToPage(int page, double? viewY = null)
    {
        if (_top.Length == 0) return;
        page = Math.Clamp(page, 0, _top.Length - 1);
        double y = _top[page] + (viewY.HasValue ? viewY.Value * _k - 24 : -TopMargin / 2);
        _sv.ScrollToVerticalOffset(Math.Max(0, y));
        CurrentPage = page;
        _tab.CurrentPage = page;
        PageChanged?.Invoke(page);
    }

    /// <summary>The page's y (view units) currently at the top of the viewport (for bookmarks).</summary>
    public (int page, double viewY) TopPosition()
    {
        var (p, frac) = Anchor(0);
        return (p, Math.Max(0, frac * (_h.Length > p ? _h[p] : 0) / _k));
    }

    public void ScrollIntoView(int page, ViewRect r)
    {
        if (_top.Length == 0) return;
        double y = _top[page] + r.Y * _k;
        double vt = _sv.VerticalOffset, vb = vt + _sv.ViewportHeight;
        if (y < vt + 40 || y + r.Height * _k > vb - BottomSpace) _sv.ScrollToVerticalOffset(Math.Max(0, y - _sv.ViewportHeight / 3));
        double x = (_contentWidth - _w[page]) / 2 + r.X * _k;
        if (x < _sv.HorizontalOffset || x > _sv.HorizontalOffset + _sv.ViewportWidth - 40)
            _sv.ScrollToHorizontalOffset(Math.Max(0, x - _sv.ViewportWidth / 3));
    }

    public void SetZoom(double zoom, double anchorViewportY = -1)
    {
        zoom = Math.Clamp(zoom, 0.1, 8);
        FitWidth = false;
        _k = zoom * 96.0 / 72.0;
        _settings.FitWidth = false;
        _settings.Zoom = zoom;
        Relayout(anchorViewportY >= 0 ? anchorViewportY : _sv.ViewportHeight * 0.3);
    }

    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(z => z > Zoom + 0.001, 8));
    public void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(z => z < Zoom - 0.001, 0.25));

    public void FitToWidth()
    {
        FitWidth = true;
        _settings.FitWidth = true;
        Relayout(_sv.ViewportHeight * 0.3);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            double factor = Math.Pow(1.1, e.Delta / 120.0);
            SetZoom(Zoom * factor, e.GetPosition(_sv).Y);
            return;
        }
        // a note or comment being typed on the page scrolls its own text
        if (InsideOwnScroller(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        double px = -e.Delta * WheelStep(_sv.ViewportHeight) * _settings.ScrollSpeed;
        if ((mods & ModifierKeys.Shift) != 0) ScrollHorizontal(px);   // Shift + wheel scrolls sideways (wheel up = left)
        else _sv.ScrollToVerticalOffset(Math.Clamp(_sv.VerticalOffset + px, 0, _sv.ScrollableHeight));
    }

    /// <summary>Pixels per unit of wheel movement (a notch is 120) at normal speed: Windows' "lines per notch"
    /// setting at 16 px a line, 48 px a notch by default; "a screen at a time" moves most of a screen.</summary>
    public static double WheelStep(double viewportHeight)
    {
        int lines = SystemParameters.WheelScrollLines;
        return lines < 0 ? Math.Max(48, viewportHeight * 0.9) / 120.0 : Math.Max(1, lines) * 16.0 / 120.0;
    }

    private bool InsideOwnScroller(DependencyObject? d)
    {
        while (d != null && !ReferenceEquals(d, _sv))
        {
            if (d is System.Windows.Controls.Primitives.TextBoxBase || d is ScrollViewer) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    public void FocusView() => Keyboard.Focus(_sv);

    /// <summary>Sideways scrolling; positive = right. Fed by Shift+wheel and by touchpad / tilt-wheel messages.</summary>
    public void ScrollHorizontal(double dx)
    {
        if (_sv.ScrollableWidth <= 0) return;
        _sv.ScrollToHorizontalOffset(Math.Clamp(_sv.HorizontalOffset + dx, 0, _sv.ScrollableWidth));
    }

    /// <summary>For the window's WM_MOUSEHWHEEL handler (two-finger sideways swipe, tilt wheel).</summary>
    public void HorizontalWheel(int delta) => ScrollHorizontal(delta * WheelStep(_sv.ViewportHeight) * _settings.ScrollSpeed);

    /// <summary>Sets the page to open at before the first layout.</summary>
    public void SetInitialPage(int page)
    {
        CurrentPage = Math.Clamp(page, 0, Math.Max(0, Doc.PageCount - 1));
        _tab.CurrentPage = CurrentPage;
    }

    // ================================================================= overlay drawing
    private void RedrawOverlay(PageElement el)
    {
        el.Overlay.Children.Clear();
        int i = el.Index;
        var hitBrush = Theme.GetBrush("SearchHit");
        var curBrush = Theme.GetBrush("SearchCurrent");
        foreach (var h in _hits)
        {
            if (h.PageIndex != i) continue;
            var brush = ReferenceEquals(h, _currentHit) ? curBrush : hitBrush;
            foreach (var r in h.Rects) AddRect(el, r.Inflate(0.6), brush, null);
        }
        if (_selPage == i && _selText != null && _moved)
        {
            int a = Math.Min(_selA, _selB), b = Math.Max(_selA, _selB);
            foreach (var r in _selText.GetRangeRects(a, b - a + 1)) AddRect(el, r, Theme.GetBrush("Selection"), null);
        }
        if (_selectedAnnot != null && _selectedAnnot.PageIndex == i)
        {
            AddRect(el, _selectedAnnot.Bounds.Inflate(2), null, Theme.GetBrush("Accent"));
            if (SelectedTextBox(el) is { } tb)
            {
                // a handle on each side: drag one to change the width
                var f = tb.Bounds.Inflate(2);
                double hh = Math.Clamp(f.Height * _k - 6, 10, 20);
                foreach (double x in new[] { f.X, f.Right })
                {
                    var handle = new Border { Width = 7, Height = hh, CornerRadius = new CornerRadius(3.5), BorderThickness = new Thickness(1.5), Background = Brushes.White };
                    handle.SetResourceReference(Border.BorderBrushProperty, "B.Accent");
                    Canvas.SetLeft(handle, x * _k - 3.5);
                    Canvas.SetTop(handle, (f.Y + f.Height / 2) * _k - hh / 2);
                    el.Overlay.Children.Add(handle);
                }
            }
        }
        if (_boxDrag?.Ghost != null && _boxDrag.El == el && !el.Overlay.Children.Contains(_boxDrag.Ghost)) el.Overlay.Children.Add(_boxDrag.Ghost);
        if (_inkLine != null && _inkPage == el && !el.Overlay.Children.Contains(_inkLine)) el.Overlay.Children.Add(_inkLine);
        if (_areaRect != null && _areaEl == el && !el.Overlay.Children.Contains(_areaRect)) el.Overlay.Children.Add(_areaRect);
    }

    private void AddRect(PageElement el, ViewRect r, Brush? fill, Brush? stroke)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(1, r.Width * _k),
            Height = Math.Max(1, r.Height * _k),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = stroke != null ? 1.5 : 0,
            StrokeDashArray = stroke != null ? new DoubleCollection { 4, 3 } : null,
            RadiusX = 2, RadiusY = 2,
        };
        Canvas.SetLeft(rect, r.X * _k);
        Canvas.SetTop(rect, r.Y * _k);
        el.Overlay.Children.Add(rect);
    }

    private void RedrawAllOverlays() { foreach (var el in _live.Values) RedrawOverlay(el); }

    // ================================================================= search highlights
    public void SetSearch(List<SearchHit> hits, SearchHit? current)
    {
        _hits = hits;
        _currentHit = current;
        RedrawAllOverlays();
        if (current != null) ScrollIntoView(current.PageIndex, current.Rects.FirstOrDefault());
    }

    // ================================================================= text & selection
    private async Task<PageText> EnsureText(PageElement el)
    {
        if (el.Text != null) return el.Text;
        var doc = Doc;
        int i = el.Index;
        var t = await PdfWorker.Shared.Run(() => doc.GetPageText(i));
        el.Text = t;
        return t;
    }

    private async Task<List<AnnotInfo>> EnsureAnnots(PageElement el)
    {
        if (el.Annots != null) return el.Annots;
        var doc = Doc;
        int i = el.Index, gen = el.AnnotGen;
        var a = await PdfWorker.Shared.Run(() => doc.GetAnnotations(i));
        if (gen == el.AnnotGen) el.Annots = a;
        return a;
    }

    public void ClearSelection()
    {
        bool had = _selPage >= 0 || _selectedAnnot != null;
        _selPage = -1; _selText = null; _moved = false;
        _selectedAnnot = null;
        if (had) RedrawAllOverlays();
    }

    public string SelectedText()
    {
        if (!HasSelection || _selText == null) return "";
        int a = Math.Min(_selA, _selB), b = Math.Max(_selA, _selB);
        return _selText.GetRangeText(a, b - a + 1);
    }

    public bool CopySelection()
    {
        var t = SelectedText();
        if (string.IsNullOrEmpty(t)) return false;
        try { Clipboard.SetText(t); } catch { }
        return true;
    }

    private static ViewPoint ToView(PageElement el, MouseEventArgs e, double k)
    {
        var p = e.GetPosition(el);
        return new ViewPoint(p.X / k, p.Y / k);
    }

    private async void Page_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var el = (PageElement)sender;
        var vp = ToView(el, e, _k);
        if (_editor != null) { CommitEditor(); e.Handled = true; return; }
        HideHover();
        FocusView();
        e.Handled = true;
        var mp = e.GetPosition(el);
        var marker = el.MarkerHits.FirstOrDefault(m => m.Hit.Contains(mp));
        if (marker.Annot != null) { ShowCommentEditor(el, marker.Annot); return; }

        // text boxes: drag to move, drag a side to change the width, double-click to change the text
        if (_tool is Tool.Select or Tool.Text)
        {
            var annots = await EnsureAnnots(el);
            var sel = SelectedTextBox(el);
            var grip = sel != null ? GripAt(sel, mp, true) : BoxGrip.None;
            var box = grip != BoxGrip.None ? sel : TextBoxAt(annots, vp);
            if (box != null)
            {
                if (e.ClickCount >= 2) { ShowTextEditor(el, vp, box); return; }
                if (grip == BoxGrip.None) grip = BoxGrip.Move;
                ClearSelection();
                _selectedAnnot = box;
                RedrawOverlay(el);
                if (Mouse.LeftButton == MouseButtonState.Pressed) StartBoxDrag(el, box, grip, mp);
                return;
            }
            if (_tool == Tool.Text)
            {
                if (_selectedAnnot != null) { ClearSelection(); return; }   // the first click away only lets go of the box
                ShowTextEditor(el, vp);
                return;
            }
        }

        switch (_tool)
        {
            case Tool.Pen:
                _ink = new List<ViewPoint> { vp };
                _inkPage = el;
                _inkLine = new Polyline
                {
                    Stroke = new SolidColorBrush(ToolColor("pen")),
                    StrokeThickness = _settings.PenWidth * _k,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _inkLine.Points.Add(new Point(vp.X * _k, vp.Y * _k));
                el.Overlay.Children.Add(_inkLine);
                el.CaptureMouse();
                return;
            case Tool.Note:
                ShowNoteEditor(el, vp, null);
                return;
        }

        // select / text markup tools
        if (_tool == Tool.Select && e.ClickCount == 1)
        {
            var annots = await EnsureAnnots(el);
            var hit = HitAnnotation(annots, vp);
            if (hit != null)
            {
                ClearSelection();
                _selectedAnnot = hit;
                RedrawOverlay(el);
                if (hit.Kind == AnnotKind.Note) ShowNoteEditor(el, new ViewPoint(hit.Bounds.X, hit.Bounds.Bottom), hit);
                return;
            }
        }
        var text = await EnsureText(el);
        if (Mouse.LeftButton != MouseButtonState.Pressed && e.ClickCount < 2)
        {
            // the button was released while the text was loading: treat it as a plain click
            ClearSelection();
            return;
        }
        if (_tool == Tool.Highlight && e.ClickCount == 1 && (text.IsEmpty || !NearText(text, vp)))
        {
            // away from any text (a picture, a scanned page): drag out a rectangle to highlight
            ClearSelection();
            StartArea(el, vp);
            return;
        }
        if (text.IsEmpty)
        {
            ClearSelection();
            if (_tool != Tool.Select) Hint?.Invoke(_tab.Doc.HasOcrText(el.Index) || !text.FromOcr ? "There is no text on this part of the page." : "Recognising text on this page…");
            return;
        }
        int idx = text.HitTest(vp.X, vp.Y);
        if (idx < 0) return;
        _selectedAnnot = null;
        if (e.ClickCount >= 2)
        {
            var (ws, wc) = text.WordAt(idx);
            _selPage = el.Index; _selText = text; _selA = ws; _selB = ws + wc - 1; _moved = true; _dragging = false;
            RedrawOverlay(el);
            if (_tool != Tool.Select) await CreateMarkupFromSelection();
            return;
        }
        if (_selPage >= 0 && _selPage != el.Index && _live.TryGetValue(_selPage, out var old)) { _selPage = -1; RedrawOverlay(old); }
        _selPage = el.Index; _selText = text; _selA = _selB = idx; _moved = false; _dragging = true;
        RedrawOverlay(el);
        el.CaptureMouse();
    }

    private void Page_MouseMove(object sender, MouseEventArgs e)
    {
        var el = (PageElement)sender;
        if (_boxDrag != null && _boxDrag.El == el)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragBox(e.GetPosition(el));
            else _ = EndBoxDrag(apply: false);   // the press was lost (another window took the mouse)
            return;
        }
        if (_ink != null && _inkLine != null && _inkPage == el && e.LeftButton == MouseButtonState.Pressed)
        {
            var vp = ToView(el, e, _k);
            var last = _ink[^1];
            if (Math.Abs(last.X - vp.X) + Math.Abs(last.Y - vp.Y) < 0.6 / _k + 0.3) return;
            _ink.Add(vp);
            _inkLine.Points.Add(new Point(vp.X * _k, vp.Y * _k));
            return;
        }
        if (_areaRect != null && _areaEl == el)
        {
            var r = AreaRect(el, ToView(el, e, _k));
            Canvas.SetLeft(_areaRect, r.X * _k);
            Canvas.SetTop(_areaRect, r.Y * _k);
            _areaRect.Width = r.Width * _k;
            _areaRect.Height = r.Height * _k;
            return;
        }
        if (!_dragging && e.LeftButton != MouseButtonState.Pressed)
        {
            var hp = ToView(el, e, _k);
            UpdateCursor(el, hp, e.GetPosition(el));
            UpdateHover(el, hp);
            return;
        }
        if (!_dragging || _selText == null || _selPage != el.Index) return;
        var p = ToView(el, e, _k);
        int idx = _selText.HitTest(p.X, p.Y);
        if (idx >= 0 && idx != _selB) { _selB = idx; _moved = true; RedrawOverlay(el); }
        else if (idx >= 0 && !_moved && idx == _selA)
        {
            var c = _selText.Chars[idx];
            if (c.HasBox && !c.Box.Contains(p.X, p.Y)) { _moved = true; RedrawOverlay(el); }
        }
    }

    private async void Page_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var el = (PageElement)sender;
        if (_boxDrag != null && _boxDrag.El == el)
        {
            await EndBoxDrag(apply: true);
            return;
        }
        el.ReleaseMouseCapture();
        if (_ink != null)
        {
            var pts = _ink;
            var line = _inkLine;
            _ink = null; _inkLine = null; _inkPage = null;
            if (pts.Count >= 2)
            {
                var color = ToRgb(ToolColor("pen"));
                float width = (float)_settings.PenWidth;
                var doc = Doc;
                int page = el.Index;
                int idx = await PdfWorker.Shared.Run(() => doc.AddInk(page, new[] { (IReadOnlyList<ViewPoint>)pts }, color, width));
                AfterAnnotationAdded(page, idx);
                await Task.Delay(120); // let the re-render land before removing the live stroke
            }
            if (line != null) el.Overlay.Children.Remove(line);
            return;
        }
        if (_areaRect != null && _areaEl == el)
        {
            var r = AreaRect(el, ToView(el, e, _k));
            var live = _areaRect;
            _areaRect = null; _areaEl = null;
            if (r.Width >= 4 && r.Height >= 4)
            {
                var color = ToRgb(ToolColor("highlight"));
                var doc = Doc;
                int page = el.Index;
                int idx = await PdfWorker.Shared.Run(() => doc.AddTextMarkup(page, AnnotKind.Highlight, new[] { r }, color));
                AfterAnnotationAdded(page, idx);
                await Task.Delay(120); // let the re-render land before removing the live rectangle
            }
            el.Overlay.Children.Remove(live);
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        if (!_moved)
        {
            _selPage = -1; _selText = null;
            RedrawOverlay(el);
            return;
        }
        if (_tool is Tool.Highlight or Tool.Underline or Tool.Strike) await CreateMarkupFromSelection();
    }

    private async Task CreateMarkupFromSelection(Tool? asTool = null)
    {
        var tool = asTool ?? _tool;
        if (_selText == null || _selPage < 0) return;
        int a = Math.Min(_selA, _selB), b = Math.Max(_selA, _selB);
        var rects = _selText.GetRangeRects(a, b - a + 1);
        int page = _selPage;
        ClearSelection();
        if (rects.Count == 0) return;
        var (kind, key) = tool switch
        {
            Tool.Underline => (AnnotKind.Underline, "underline"),
            Tool.Strike => (AnnotKind.StrikeOut, "strike"),
            _ => (AnnotKind.Highlight, "highlight"),
        };
        var color = ToRgb(ToolColor(key));
        var doc = Doc;
        int idx = await PdfWorker.Shared.Run(() => doc.AddTextMarkup(page, kind, rects, color));
        AfterAnnotationAdded(page, idx);
    }

    private void AfterAnnotationAdded(int page, int idx)
    {
        if (idx < 0) return;
        _tab.UndoStack.Add((page, idx));
        if (_live.TryGetValue(page, out var el)) ReloadMarkers(el);
        Edited?.Invoke(page);
        ScheduleRender();
    }

    private static AnnotInfo? HitAnnotation(List<AnnotInfo> annots, ViewPoint p)
    {
        AnnotInfo? best = null;
        foreach (var a in annots)
        {
            if (!a.Bounds.Inflate(2).Contains(p.X, p.Y)) continue;
            // only notes, text boxes and drawings are picked by a left click;
            // highlights stay selectable as text (right-click them to delete)
            if (a.Kind is AnnotKind.Note or AnnotKind.FreeText or AnnotKind.Ink) best = a;
        }
        return best;
    }

    public bool HasSelectedAnnotation => _selectedAnnot != null;

    public async Task<bool> DeleteSelectedAnnotation()
    {
        var a = _selectedAnnot;
        if (a == null) return false;
        _selectedAnnot = null;
        await DeleteAnnotation(a.PageIndex, a.Index);
        return true;
    }

    public async Task DeleteAnnotation(int page, int index)
    {
        var doc = Doc;
        bool ok = await PdfWorker.Shared.Run(() => doc.RemoveAnnotation(page, index));
        if (!ok) return;
        _tab.OnAnnotationRemoved(page, index);
        if (_live.TryGetValue(page, out var el)) { ReloadMarkers(el); RedrawOverlay(el); }
        Edited?.Invoke(page);
        ScheduleRender();
    }

    public async Task<bool> Undo()
    {
        if (_tab.UndoStack.Count == 0) return false;
        var (page, index) = _tab.UndoStack[^1];
        await DeleteAnnotation(page, index);
        return true;
    }

    public async void ApplyMarkupToSelection(Tool tool)
    {
        if (!HasSelection) return;
        await CreateMarkupFromSelection(tool);
    }

    // ================================================================= context menu
    /// <summary>Lets the window append the general rows (tools, colours, undo, save, print) to every right-click menu.</summary>
    public event Action<PopupMenu>? MenuExtras;

    /// <summary>The page the open right-click menu belongs to (-1: the gap between pages).</summary>
    public int MenuPage { get; private set; } = -1;

    /// <summary>The selection's "Look up" row: the window opens Google AI Mode with this text.</summary>
    public event Action<string>? LookUpRequested;

    private void Canvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        MenuPage = -1;
        var menu = new PopupMenu();
        MenuExtras?.Invoke(menu);
        menu.Open(_canvas);
    }

    private async void Page_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var el = (PageElement)sender;
        var vp = ToView(el, e, _k);
        e.Handled = true;
        var menu = new PopupMenu();
        if (HasSelection && _selPage == el.Index)
        {
            menu.Item("Copy", Shortcuts.Text("copy"), () => CopySelection(), Icons.Copy);
            var lookUpText = SelectedText();
            menu.Item("Look up", Shortcuts.Text("lookup"), () => LookUpRequested?.Invoke(lookUpText), Icons.LookUp);
            menu.Mark("lookup");
            menu.Separator();
            menu.Item("Highlight", null, () => ApplyMarkupToSelection(Tool.Highlight), Icons.ToolHighlight, Swatch("highlight"));
            menu.Item("Underline", null, () => ApplyMarkupToSelection(Tool.Underline), Icons.ToolUnderline, Swatch("underline"));
            menu.Item("Strikethrough", null, () => ApplyMarkupToSelection(Tool.Strike), Icons.ToolStrike, Swatch("strike"));
        }
        else
        {
            var annots = await EnsureAnnots(el);
            var hit = annots.Where(a => a.Bounds.Inflate(2).Contains(vp.X, vp.Y)).LastOrDefault();
            if (hit != null)
            {
                _selectedAnnot = hit;
                RedrawOverlay(el);
                if (hit.Kind == AnnotKind.Note)
                    menu.Item("Edit note", null, () => ShowNoteEditor(el, new ViewPoint(hit.Bounds.X, hit.Bounds.Bottom), hit), Icons.Edit);
                if (CanComment(hit))
                    menu.Item(CommentOf(hit).Length > 0 ? "Edit comment" : "Add comment", null, () => ShowCommentEditor(el, hit), Icons.Comment);
                if (!string.IsNullOrWhiteSpace(hit.MarkedText))
                    menu.Item("Copy marked text", null, () => { try { Clipboard.SetText(hit.MarkedText); } catch { } }, Icons.Copy);
                menu.Item("Delete", Shortcuts.Text("delete"), async () => { _selectedAnnot = null; await DeleteAnnotation(hit.PageIndex, hit.Index); }, Icons.Trash);
            }
            else
            {
                menu.Item("Add note here", null, () => ShowNoteEditor(el, vp, null), Icons.ToolNote, Swatch("note"));
                menu.Item("Add text box here", null, () => ShowTextEditor(el, vp), Icons.ToolText, Swatch("text"));
            }
        }
        menu.Separator();
        MenuPage = el.Index;
        MenuExtras?.Invoke(menu);
        menu.Open(el);
    }

    private Brush Swatch(string key) => new SolidColorBrush(ToolColor(key));

    // ================================================================= notes & text boxes
    private Color ToolColor(string key)
    {
        var hex = _settings.ToolColors.TryGetValue(key, out var h) ? h : "#FFD60A";
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);

    private void PlaceEditor(PageElement el, FrameworkElement editor, ViewPoint at)
    {
        CancelEditor();
        _editor = editor;
        double x = Math.Min(at.X * _k, Math.Max(0, _w[el.Index] - 250));
        Canvas.SetLeft(editor, Math.Max(0, x));
        Canvas.SetTop(editor, at.Y * _k);
        el.Editors.Children.Add(editor);
    }

    public void CancelEditor()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        if (ed.Parent is Canvas c) c.Children.Remove(ed);
        FocusView();
    }

    private Action? _commit;
    private void CommitEditor()
    {
        var c = _commit;
        _commit = null;
        c?.Invoke();
        CancelEditor();
    }

    private void ShowNoteEditor(PageElement el, ViewPoint at, AnnotInfo? existing)
    {
        var box = new TextBox
        {
            Text = existing?.Contents ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            FontSize = 13,
            Padding = new Thickness(0),
        };
        var title = new TextBlock
        {
            Text = existing == null ? "New note" : $"Note · p. {el.Index + 1}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        Button Btn(string text, bool accent)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.Resources[accent ? "AccentButton" : "PlainButton"], Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 0, 0), FontSize = 12 };
            return b;
        }
        if (existing != null)
        {
            var del = Btn("Delete", false);
            del.Click += async (_, _) => { _commit = null; CancelEditor(); _selectedAnnot = null; await DeleteAnnotation(existing.PageIndex, existing.Index); };
            buttons.Children.Add(del);
        }
        var cancel = Btn("Cancel", false);
        cancel.Click += (_, _) => { _commit = null; CancelEditor(); };
        var save = Btn("Save", true);
        save.Click += (_, _) => CommitEditor();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        var card = new Border
        {
            Width = 250,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xB0)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
        };
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        int page = el.Index;
        _commit = async () =>
        {
            var text = box.Text.Trim();
            var doc = Doc;
            if (existing != null)
            {
                if (text == existing.Contents) return;
                await PdfWorker.Shared.Run(() => doc.SetAnnotationContents(existing.PageIndex, existing.Index, text));
                if (_live.TryGetValue(page, out var e1)) ReloadMarkers(e1);
                Edited?.Invoke(page);
                return;
            }
            if (text.Length == 0) return;
            var color = ToRgb(ToolColor("note"));
            int idx = await PdfWorker.Shared.Run(() => doc.AddNote(page, at, text, color));
            AfterAnnotationAdded(page, idx);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _commit = null; CancelEditor(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CommitEditor(); e.Handled = true; }
        };
        PlaceEditor(el, card, existing == null ? at : new ViewPoint(at.X, at.Y + 4));
        box.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
    }

    // ================================================================= comments on highlights, underlines, drawings
    // A markup's comment is its /Contents, the field Acrobat, Edge, Preview and other readers show as its comment.
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private PageElement? _hoverEl;
    private AnnotInfo? _hoverAnnot;
    private Border? _hoverCard;
    private static readonly Geometry MarkerShape = Frozen("M3 1h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H8l-3.5 3V11H3a2 2 0 0 1-2-2V3a2 2 0 0 1 2-2z");

    private static Geometry Frozen(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    public static bool CanComment(AnnotInfo a) =>
        a.Kind is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut or AnnotKind.Squiggly or AnnotKind.Ink;

    /// <summary>The comment on a markup, or "". Some apps store the marked text itself in /Contents; that isn't a comment.</summary>
    public static string CommentOf(AnnotInfo a)
    {
        if (!CanComment(a)) return "";
        var c = (a.Contents ?? "").Trim();
        if (c.Length == 0) return "";
        return Squash(c) == Squash(a.MarkedText) ? "" : c;
    }

    private static string Squash(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string KindName(AnnotKind k) => k switch
    {
        AnnotKind.Highlight => "Highlight",
        AnnotKind.Underline => "Underline",
        AnnotKind.StrikeOut => "Strikethrough",
        AnnotKind.Squiggly => "Squiggly underline",
        AnnotKind.Ink => "Drawing",
        _ => "Markup",
    };

    /// <summary>The markup's own colour, a shade deeper so the bubble reads on top of the highlight.</summary>
    private static Color MarkerColor(AnnotInfo a)
    {
        uint c = a.ColorArgb;
        if ((c & 0xFFFFFF) == 0) return Color.FromRgb(0xFF, 0x9F, 0x0A);
        return Color.FromRgb((byte)(((c >> 16) & 0xFF) * 0.82), (byte)(((c >> 8) & 0xFF) * 0.82), (byte)((c & 0xFF) * 0.82));
    }

    /// <summary>A small speech bubble at the top-right corner of every markup that carries a comment.</summary>
    private void DrawMarkers(PageElement el)
    {
        el.MarkerLayer.Children.Clear();
        el.MarkerHits.Clear();
        if (el.Annots == null) { _ = LoadMarkers(el); return; }
        foreach (var a in el.Annots)
        {
            if (CommentOf(a).Length == 0) continue;
            double x = Math.Max(0, Math.Min(a.Bounds.Right * _k - 6, el.Width - 17));
            double y = Math.Max(0, a.Bounds.Y * _k - 13);
            var bubble = new Path
            {
                Data = MarkerShape,
                Width = 16,
                Height = 15,
                Stretch = Stretch.Fill,
                Fill = new SolidColorBrush(MarkerColor(a)),
                Stroke = Brushes.White,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
            };
            Canvas.SetLeft(bubble, x);
            Canvas.SetTop(bubble, y);
            el.MarkerLayer.Children.Add(bubble);
            el.MarkerHits.Add((a, new Rect(x - 4, y - 4, 24, 23)));
        }
    }

    /// <summary>Reads the page's annotations in the background, then draws the comment bubbles.</summary>
    private async Task LoadMarkers(PageElement el)
    {
        if (el.MarkersLoading) return;
        el.MarkersLoading = true;
        try
        {
            while (el.Live && el.Annots == null)
            {
                int i = el.Index, gen = el.AnnotGen;
                var doc = Doc;
                var list = await PdfWorker.Shared.Run(() => doc.GetAnnotations(i), background: true);
                if (gen == el.AnnotGen && el.Annots == null) el.Annots = list;
            }
        }
        catch { return; }
        finally { el.MarkersLoading = false; }
        if (el.Live) DrawMarkers(el);
    }

    /// <summary>The page's annotations changed: read them again (the old bubbles stay until the new list is in).</summary>
    private void ReloadMarkers(PageElement el)
    {
        el.Annots = null;
        el.AnnotGen++;
        if (_hoverEl == el) HideHover();
        _ = LoadMarkers(el);
    }

    private void UpdateHover(PageElement el, ViewPoint p)
    {
        AnnotInfo? hit = null;
        if (_editor == null && el.Annots != null)
        {
            var px = new Point(p.X * _k, p.Y * _k);
            foreach (var m in el.MarkerHits) if (m.Hit.Contains(px)) hit = m.Annot;
            if (hit == null)
                foreach (var a in el.Annots)
                    if (a.Bounds.Inflate(1).Contains(p.X, p.Y) && CommentOf(a).Length > 0) hit = a;
        }
        if (hit != null && _hoverEl == el && _hoverAnnot != null && _hoverAnnot.PageIndex == hit.PageIndex && _hoverAnnot.Index == hit.Index) return;
        HideHover();
        if (hit == null) return;
        _hoverEl = el;
        _hoverAnnot = hit;
        _hoverTimer.Start();
    }

    private void HideHover()
    {
        _hoverTimer.Stop();
        if (_hoverCard?.Parent is Canvas c) c.Children.Remove(_hoverCard);
        if (_hoverEl != null) Panel.SetZIndex(_hoverEl, 0);
        _hoverCard = null;
        _hoverEl = null;
        _hoverAnnot = null;
    }

    /// <summary>The comment, in a small card under the markup (above it near the bottom of the page).</summary>
    private void ShowHoverCard()
    {
        var el = _hoverEl;
        var a = _hoverAnnot;
        if (el == null || a == null || !el.Live || _editor != null) return;
        var text = CommentOf(a);
        if (text.Length == 0) return;
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 17, MaxHeight = 17 * 8, TextTrimming = TextTrimming.CharacterEllipsis };
        body.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        var card = new Border
        {
            MaxWidth = 300,
            Padding = new Thickness(11, 8, 11, 9),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Child = body,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.18 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "B.MenuBg");
        card.SetResourceReference(Border.BorderBrushProperty, "B.Hair");
        card.Measure(new Size(300, double.PositiveInfinity));
        double h = card.DesiredSize.Height, w = card.DesiredSize.Width;
        double x = Math.Max(0, Math.Min(a.Bounds.X * _k, el.Width - w));
        double below = a.Bounds.Bottom * _k + 6, above = a.Bounds.Y * _k - 6 - h;
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, below + h > el.Height && above >= 0 ? above : below);
        el.Editors.Children.Add(card);
        Panel.SetZIndex(el, 2);   // over the next page
        _hoverCard = card;
    }

    private static Button EditorButton(string text, bool accent) => new()
    {
        Content = text,
        Style = (Style)Application.Current.Resources[accent ? "AccentButton" : "PlainButton"],
        Height = 28,
        Padding = new Thickness(12, 0, 12, 0),
        Margin = new Thickness(6, 0, 0, 0),
        FontSize = 12,
    };

    /// <summary>Adds, edits or removes the comment on a highlight (or underline, strikethrough, drawing).</summary>
    private void ShowCommentEditor(PageElement el, AnnotInfo annot)
    {
        HideHover();
        _selectedAnnot = annot;
        RedrawOverlay(el);
        string existing = CommentOf(annot);
        var box = new TextBox
        {
            Text = existing,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            FontSize = 13,
            Padding = new Thickness(0),
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(MarkerColor(annot)), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock
        {
            Text = $"Comment · {KindName(annot.Kind)} · p. {PageName.Of(annot.PageIndex)}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        if (existing.Length > 0)
        {
            var remove = EditorButton("Remove", false);
            remove.Click += async (_, _) => { _commit = null; CancelEditor(); await SaveComment(annot, ""); };
            buttons.Children.Add(remove);
        }
        var cancel = EditorButton("Cancel", false);
        cancel.Click += (_, _) => { _commit = null; CancelEditor(); };
        var save = EditorButton("Save", true);
        save.Click += (_, _) => CommitEditor();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        var card = new Border
        {
            Width = 260,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xDE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
        };
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _commit = async () =>
        {
            var text = box.Text.Trim();
            if (text == existing) return;
            await SaveComment(annot, text);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _commit = null; CancelEditor(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CommitEditor(); e.Handled = true; }
        };
        PlaceEditor(el, card, new ViewPoint(annot.Bounds.X, annot.Bounds.Bottom + 4));
        box.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
    }

    private async Task SaveComment(AnnotInfo annot, string text)
    {
        var doc = Doc;
        bool ok = await PdfWorker.Shared.Run(() => doc.SetAnnotationContents(annot.PageIndex, annot.Index, text));
        if (!ok) return;
        if (_live.TryGetValue(annot.PageIndex, out var el)) ReloadMarkers(el);
        Edited?.Invoke(annot.PageIndex);
        ScheduleRender();
    }

    // ================================================================= area highlights (pictures, scanned pages)
    private PageElement? _areaEl;
    private ViewPoint _areaStart;
    private Rectangle? _areaRect;

    /// <summary>True when the point is on or near text, where a drag selects words instead of drawing a box.</summary>
    private static bool NearText(PageText text, ViewPoint p)
    {
        foreach (var c in text.Chars)
            if (c.HasBox && c.Box.Inflate(8).Contains(p.X, p.Y)) return true;
        return false;
    }

    private void StartArea(PageElement el, ViewPoint p)
    {
        var c = ToolColor("highlight");
        _areaEl = el;
        _areaStart = p;
        _areaRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)),
            Stroke = new SolidColorBrush(c),
            StrokeThickness = 1,
            RadiusX = 2,
            RadiusY = 2,
            Width = 0,
            Height = 0,
        };
        Canvas.SetLeft(_areaRect, p.X * _k);
        Canvas.SetTop(_areaRect, p.Y * _k);
        el.Overlay.Children.Add(_areaRect);
        el.CaptureMouse();
    }

    /// <summary>The dragged box in view units, kept on the page.</summary>
    private ViewRect AreaRect(PageElement el, ViewPoint p)
    {
        double maxX = _w[el.Index] / _k, maxY = _h[el.Index] / _k;
        double x1 = Math.Clamp(Math.Min(_areaStart.X, p.X), 0, maxX), x2 = Math.Clamp(Math.Max(_areaStart.X, p.X), 0, maxX);
        double y1 = Math.Clamp(Math.Min(_areaStart.Y, p.Y), 0, maxY), y2 = Math.Clamp(Math.Max(_areaStart.Y, p.Y), 0, maxY);
        return new ViewRect(x1, y1, x2 - x1, y2 - y1);
    }

    private const double TextBoxWidth = 260; // view units (points)

    /// <summary>Types a new text box at <paramref name="at"/>, or changes the text of <paramref name="existing"/>
    /// (which keeps its place, width, size and colour; emptied, it is deleted).</summary>
    private void ShowTextEditor(PageElement el, ViewPoint at, AnnotInfo? existing = null)
    {
        double size = existing is { FontSize: > 0 } ? existing.FontSize : _settings.TextSize;
        var color = existing != null ? BoxColor(existing) : ToolColor("text");
        double width = existing != null ? Math.Max(60, existing.Bounds.Width) : TextBoxWidth;
        var box = new TextBox
        {
            Text = existing?.Contents ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = width * _k,
            MinHeight = size * 1.6 * _k,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size * _k,
            Foreground = new SolidColorBrush(color),
            // over an existing box: paper-coloured, so the old text doesn't show through while typing
            Background = existing != null ? new SolidColorBrush(Theme.PaperColor(Theme.Current, Theme.TintPages)) : new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x66, 0xCC)),
            BorderBrush = Theme.GetBrush("Accent"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            CaretBrush = new SolidColorBrush(color),
        };
        int page = el.Index;
        _commit = async () =>
        {
            var text = box.Text.TrimEnd();
            if (existing != null)
            {
                if (text.Trim().Length == 0) { _selectedAnnot = null; await DeleteAnnotation(page, existing.Index); return; }
                if (text == existing.Contents) return;
                bool ok = await RelayoutTextBox(page, existing, text, existing.Bounds.X, existing.Bounds.Y, width);
                await AfterBoxChanged(el, existing.Index, ok);
                return;
            }
            if (text.Trim().Length == 0) return;
            var origin = new ViewPoint(at.X + 1 / _k, at.Y + 1 / _k);
            var glyphs = BuildGlyphs(text, origin, size, TextBoxWidth - 2, out var bounds);
            var rgb = ToRgb(color);
            var doc = Doc;
            var rect = new ViewRect(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4);
            int idx = await PdfWorker.Shared.Run(() => doc.AddFreeText(page, rect, text, glyphs, rgb, size));
            AfterAnnotationAdded(page, idx);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _commit = null; CancelEditor(); e.Handled = true; }
            // Enter saves the text box and finishes; Shift+Enter starts a new line
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { CommitEditor(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (_editor == box) CommitEditor(); };
        box.MouseLeftButtonDown += (_, e) => e.Handled = true;
        PlaceEditor(el, box, existing != null ? new ViewPoint(existing.Bounds.X, existing.Bounds.Y) : at);
        box.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
    }

    // ================================================================= moving and resizing text boxes
    private enum BoxGrip { None, Move, Left, Right }

    private sealed class BoxDrag
    {
        public required PageElement El;
        public required AnnotInfo Box;
        public required BoxGrip Grip;
        public required Point Start;      // element pixels
        public ViewRect Result;
        public bool Moved;
        public Border? Ghost;
    }

    private BoxDrag? _boxDrag;
    private const double MinBoxWidth = 24;   // view units

    private AnnotInfo? SelectedTextBox(PageElement el) =>
        _selectedAnnot is { Kind: AnnotKind.FreeText } a && a.PageIndex == el.Index && _tool is Tool.Select or Tool.Text ? a : null;

    /// <summary>The text box under the point, topmost first.</summary>
    private static AnnotInfo? TextBoxAt(List<AnnotInfo>? annots, ViewPoint p)
    {
        if (annots == null) return null;
        for (int i = annots.Count - 1; i >= 0; i--)
            if (annots[i].Kind == AnnotKind.FreeText && annots[i].Bounds.Inflate(2).Contains(p.X, p.Y)) return annots[i];
        return null;
    }

    /// <summary>What a press at <paramref name="mp"/> (element pixels) would grab: a side of the selected box (to change
    /// its width), or the box itself (to move it).</summary>
    private BoxGrip GripAt(AnnotInfo box, Point mp, bool selected)
    {
        var f = box.Bounds.Inflate(2);
        double x1 = f.X * _k, x2 = f.Right * _k, y1 = f.Y * _k, y2 = f.Bottom * _k;
        if (mp.Y < y1 - 6 || mp.Y > y2 + 6) return BoxGrip.None;
        if (selected && Math.Abs(mp.X - x1) <= 6) return BoxGrip.Left;
        if (selected && Math.Abs(mp.X - x2) <= 6) return BoxGrip.Right;
        return mp.X >= x1 && mp.X <= x2 && mp.Y >= y1 && mp.Y <= y2 ? BoxGrip.Move : BoxGrip.None;
    }

    private Color BoxColor(AnnotInfo box) => box.TextArgb != 0
        ? Color.FromRgb((byte)(box.TextArgb >> 16), (byte)(box.TextArgb >> 8), (byte)box.TextArgb)
        : ToolColor("text");

    private void StartBoxDrag(PageElement el, AnnotInfo box, BoxGrip grip, Point start)
    {
        _boxDrag = new BoxDrag { El = el, Box = box, Grip = grip, Start = start, Result = box.Bounds };
        el.CaptureMouse();
    }

    private void DragBox(Point p)
    {
        var d = _boxDrag!;
        if (!d.Moved && Math.Abs(p.X - d.Start.X) + Math.Abs(p.Y - d.Start.Y) < 4) return;
        d.Moved = true;
        double dx = (p.X - d.Start.X) / _k, dy = (p.Y - d.Start.Y) / _k;
        double pw = _w[d.El.Index] / _k, ph = _h[d.El.Index] / _k;
        var b = d.Box.Bounds;
        switch (d.Grip)
        {
            case BoxGrip.Left:
            {
                double left = Math.Clamp(b.X + dx, 0, b.Right - MinBoxWidth);
                d.Result = new ViewRect(left, b.Y, b.Right - left, b.Height);
                break;
            }
            case BoxGrip.Right:
            {
                double right = Math.Clamp(b.Right + dx, b.X + MinBoxWidth, Math.Max(b.X + MinBoxWidth, pw));
                d.Result = new ViewRect(b.X, b.Y, right - b.X, b.Height);
                break;
            }
            default:
                d.Result = new ViewRect(Math.Clamp(b.X + dx, 0, Math.Max(0, pw - b.Width)), Math.Clamp(b.Y + dy, 0, Math.Max(0, ph - b.Height)), b.Width, b.Height);
                break;
        }
        ShowGhost(d);
    }

    /// <summary>Where the box will go: a dashed frame with its text. While a side is dragged, the text wraps to the new
    /// width and the frame's height follows it, as the box will.</summary>
    private void ShowGhost(BoxDrag d)
    {
        if (d.Ghost == null)
        {
            double size = d.Box.FontSize > 0 ? d.Box.FontSize : _settings.TextSize;
            var text = new TextBlock
            {
                Text = d.Box.Contents, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Segoe UI"),
                FontSize = size * _k, Foreground = new SolidColorBrush(BoxColor(d.Box)), Opacity = 0.75, Margin = new Thickness(2 * _k),
            };
            d.Ghost = new Border
            {
                Child = text, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(2), IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x66, 0xCC)),
            };
            d.Ghost.SetResourceReference(Border.BorderBrushProperty, "B.Accent");
        }
        if (!d.El.Overlay.Children.Contains(d.Ghost)) d.El.Overlay.Children.Add(d.Ghost);
        var r = d.Result;
        d.Ghost.Width = r.Width * _k;
        d.Ghost.Height = d.Grip == BoxGrip.Move ? r.Height * _k : double.NaN;
        Canvas.SetLeft(d.Ghost, r.X * _k);
        Canvas.SetTop(d.Ghost, r.Y * _k);
    }

    private async Task EndBoxDrag(bool apply)
    {
        var d = _boxDrag;
        if (d == null) return;
        _boxDrag = null;
        if (d.El.IsMouseCaptured) d.El.ReleaseMouseCapture();
        if (apply && d.Moved)
        {
            var box = d.Box;
            int page = d.El.Index;
            bool ok;
            if (d.Grip == BoxGrip.Move)
            {
                double dx = d.Result.X - box.Bounds.X, dy = d.Result.Y - box.Bounds.Y;
                var doc = Doc;
                ok = await PdfWorker.Shared.Run(() => doc.MoveAnnotation(page, box.Index, dx, dy));
            }
            else ok = await RelayoutTextBox(page, box, box.Contents, d.Result.X, box.Bounds.Y, d.Result.Width);
            await AfterBoxChanged(d.El, box.Index, ok);
            await Task.Delay(120);   // let the re-render land before the preview goes
        }
        if (d.Ghost != null) d.El.Overlay.Children.Remove(d.Ghost);
    }

    /// <summary>Lays a text box's text out again at <paramref name="width"/> (its height follows the text), with its top
    /// left corner where it was, and stores it.</summary>
    private async Task<bool> RelayoutTextBox(int page, AnnotInfo box, string text, double left, double top, double width)
    {
        double size = box.FontSize > 0 ? box.FontSize : _settings.TextSize;
        double wrap = Math.Max(10, width - 4);
        BuildGlyphs(text, new ViewPoint(left + 2, top + 2), size, wrap, out var first);
        // shift so the text's own edges sit where the box starts: the box doesn't creep with each change
        var origin = new ViewPoint(left + 2 + (left + 2 - first.X), top + 2 + (top + 2 - first.Y));
        var glyphs = BuildGlyphs(text, origin, size, wrap, out var bounds);
        var rect = new ViewRect(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4);
        var rgb = ToRgb(BoxColor(box));
        var doc = Doc;
        int idx = box.Index;
        return await PdfWorker.Shared.Run(() => doc.UpdateFreeText(page, idx, rect, text, glyphs, rgb));
    }

    private async Task AfterBoxChanged(PageElement el, int annotIndex, bool ok)
    {
        if (!ok) { Hint?.Invoke("This text box can't be changed."); return; }
        ReloadMarkers(el);
        Edited?.Invoke(el.Index);
        ScheduleRender();
        var annots = await EnsureAnnots(el);
        _selectedAnnot = annots.FirstOrDefault(a => a.Index == annotIndex);
        RedrawOverlay(el);
    }

    /// <summary>The pointer shows what a press will do. Highlight: an I-beam over text, a cross where a box is dragged
    /// out. Select and Text tools: move arrows over a text box, sideways arrows on the selected box's sides.</summary>
    private void UpdateCursor(PageElement el, ViewPoint vp, Point mp)
    {
        Cursor? c = null;
        if (_tool is Tool.Select or Tool.Text)
        {
            if (el.Annots == null) _ = EnsureAnnots(el);
            else
            {
                var sel = SelectedTextBox(el);
                var grip = sel != null ? GripAt(sel, mp, true) : BoxGrip.None;
                if (grip is BoxGrip.Left or BoxGrip.Right) c = Cursors.SizeWE;
                else if (grip == BoxGrip.Move || TextBoxAt(el.Annots, vp) != null) c = Cursors.SizeAll;
            }
        }
        else if (_tool == Tool.Highlight)
        {
            if (el.Text == null) _ = EnsureText(el);
            else c = !el.Text.IsEmpty && NearText(el.Text, vp) ? Cursors.IBeam : Cursors.Cross;
        }
        if (!ReferenceEquals(el.Cursor, c)) el.Cursor = c;
    }


    /// <summary>Turns text into vector outlines (view units) so the text box looks the same in every PDF reader.</summary>
    private static List<PathCommand> BuildGlyphs(string text, ViewPoint origin, double size, double maxWidth, out Rect bounds)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, 1.0)
        { MaxTextWidth = Math.Max(10, maxWidth) };
        var geo = ft.BuildGeometry(new Point(origin.X, origin.Y));
        bounds = geo.Bounds.IsEmpty ? new Rect(origin.X, origin.Y, 10, size) : geo.Bounds;
        var flat = geo.GetFlattenedPathGeometry(0.03, ToleranceType.Absolute);
        var cmds = new List<PathCommand>();
        foreach (var fig in flat.Figures)
        {
            cmds.Add(PathCommand.Move(fig.StartPoint.X, fig.StartPoint.Y));
            foreach (var seg in fig.Segments)
            {
                switch (seg)
                {
                    case PolyLineSegment pl:
                        foreach (var pt in pl.Points) cmds.Add(PathCommand.Line(pt.X, pt.Y));
                        break;
                    case LineSegment ls:
                        cmds.Add(PathCommand.Line(ls.Point.X, ls.Point.Y));
                        break;
                    case PolyBezierSegment pb:
                        for (int j = 0; j + 2 < pb.Points.Count; j += 3)
                            cmds.Add(PathCommand.Cubic(pb.Points[j].X, pb.Points[j].Y, pb.Points[j + 1].X, pb.Points[j + 1].Y, pb.Points[j + 2].X, pb.Points[j + 2].Y));
                        break;
                    case BezierSegment bz:
                        cmds.Add(PathCommand.Cubic(bz.Point1.X, bz.Point1.Y, bz.Point2.X, bz.Point2.Y, bz.Point3.X, bz.Point3.Y));
                        break;
                }
            }
            if (fig.IsClosed) cmds.Add(PathCommand.Close());
        }
        return cmds;
    }

    public bool HasEditor => _editor != null;
}
