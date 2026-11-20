using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Rodspaette.App;

/// <summary>The bottom tool bar as docked Look up uses it: where its buttons are, and the room it opens above or below them.</summary>
public sealed class ToolbarDock
{
    /// <summary>The tool bar's outline without any opened room, in host coordinates; null while the tool bar is hidden.</summary>
    public required Func<Rect?> Outline { get; init; }
    /// <summary>Empty rows above and below the buttons. Their height is the room the page gets.</summary>
    public required FrameworkElement RoomAbove { get; init; }
    public required FrameworkElement RoomBelow { get; init; }
    /// <summary>Moves the tool bar down while the room below opens, so its top edge stays where it is.</summary>
    public required TranslateTransform Drop { get; init; }
    /// <summary>Border plus padding between the tool bar's edge and its contents.</summary>
    public double Inset { get; init; } = 6;
}

/// <summary>
/// "Look up" (Ctrl+L, the selection's right-click menu, the function dial): Google Search in AI Mode, and nothing else.
/// Docked (the default), the bottom tool bar's top edge rises to make room and the page fades in once it has; closing
/// reverses that. In undocked mode (Settings) it opens beside the mouse as a card with a drag rim and resize edges, and
/// that size is remembered. Esc or a click anywhere else closes it. With "Tool bar transparency" on it fades a little
/// while the mouse is away (docked, together with the tool bar).
/// The page is drawn by WebView2's composition control (the Edge engine that comes with Windows), so it fades and clips
/// like the rest of the window. Its profile lives under %LOCALAPPDATA%\Rødspætte PDF\WebView2, so a Google consent choice
/// or sign-in is remembered. Links that leave Google open in the default browser.
/// </summary>
public sealed class LookUpPanel : Grid
{
    /// <summary>How see-through the tool bar and this panel get with "Tool bar transparency" on.</summary>
    public const double GlassOpacity = 0.72;
    private const double Edge = 12, MinW = 340, MinH = 300, Gap = 5, MinRoom = 220, MaxRoom = 690;
    private const string GripIcon = "M19 12l-7 7 M19 16.5l-2.5 2.5";
    private const string GetRuntimeUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string HomeUrl = "https://www.google.com/ai";
    private const string EscMessage = "rodspaette:esc";
    private static readonly TimeSpan RiseTime = TimeSpan.FromMilliseconds(280), SinkTime = TimeSpan.FromMilliseconds(220);

    /// <summary>Keys typed in the page, reported to the app: any key keeps the glass solid ("rodspaette:key", at most
    /// every 200 ms); keys with Ctrl or Alt, and F-keys, are passed on so Look up's own shortcut closes it
    /// ("rodspaette:chord:" + Ctrl Alt Shift as 1/0 + ":" + the key's code); Esc that the page doesn't use closes it.</summary>
    private const string EscScript = """
        (() => {
          if (window.top !== window || !(window.chrome && chrome.webview)) return;
          let last = 0;
          window.addEventListener('keydown', e => {
            const now = Date.now();
            if (now - last > 200) { last = now; chrome.webview.postMessage('rodspaette:key'); }
            if (e.ctrlKey || e.altKey || /^F\d+$/.test(e.code))
              chrome.webview.postMessage('rodspaette:chord:' + (e.ctrlKey ? 1 : 0) + (e.altKey ? 1 : 0) + (e.shiftKey ? 1 : 0) + ':' + e.code);
          }, true);
          window.addEventListener('keydown', e => {
            if (e.key === 'Escape' && !e.defaultPrevented) chrome.webview.postMessage('rodspaette:esc');
          });
        })();
        """;

    private static readonly Regex GoogleHost = new(@"(^|\.)google\.(com|[a-z]{2}|co\.[a-z]{2}|com\.[a-z]{2})$|^accounts\.youtube\.com$",
                                                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static CoreWebView2Environment? _env;

    private enum Look { Card, Docked }

    private readonly AppSettings _s;
    private readonly FrameworkElement _host;
    private readonly ToolbarDock _dock;
    private readonly Grid _body, _content;
    private readonly Border _card, _rim;
    private readonly WebView2CompositionControl _web;
    private readonly StackPanel _missing;
    private readonly FrameworkElement[] _undockedOnly;
    private readonly ScaleTransform _scale = new(1, 1);
    private Look _look;
    private bool _open, _started, _ready, _hasPage, _moving, _up;
    private int _gen;
    private double _room;
    private string? _pending, _lastUrl;
    private Point _moveStart;
    private Thickness _moveOrigin;

    /// <summary>Raised when the panel closes, so the window can give the keyboard back to the page.</summary>
    public event Action? Closed;

    /// <summary>Docked: the mouse moved onto or off the panel, or it opened or closed (the window fades the tool bar with it).</summary>
    public event Action? HoverChanged;

    /// <summary>A key with Ctrl or Alt (or an F-key) was pressed in the Google page.</summary>
    public event Action<KeyChord>? ChordPressed;

    /// <summary>Typing keeps the glass solid for a moment (Tool bar transparency).</summary>
    public TypingGlass? Typing { get; set; }

    public bool IsOpen => _open;

    /// <summary>Open and grown out of the tool bar (so the tool bar counts as part of it).</summary>
    public bool IsDocked => _open && _look == Look.Docked;

    private static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rødspætte PDF", "WebView2");

    /// <param name="s">Settings: docked or undocked, transparency, the undocked size.</param>
    /// <param name="host">The reading area the panel lives in.</param>
    /// <param name="dock">The bottom tool bar.</param>
    public LookUpPanel(AppSettings s, FrameworkElement host, ToolbarDock dock)
    {
        _s = s;
        _host = host;
        _dock = dock;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        UseLayoutRounding = true;

        _body = new Grid();
        Children.Add(_body);

        // ---- undocked: a card in the tool bar's look (frosted, hairline edge, soft shadow)
        _card = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.16 },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "B.Glass");
        _card.SetResourceReference(Border.BorderBrushProperty, "B.Hair");
        _body.Children.Add(_card);

        _content = new Grid();
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // rim
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // page
        _body.Children.Add(_content);

        // ---- top rim: the handle that moves the card
        var pill = new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        pill.SetResourceReference(Border.BackgroundProperty, "B.Edge");
        _rim = new Border { Height = 14, Margin = new Thickness(0, -4, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Child = pill };
        _rim.MouseLeftButtonDown += Rim_MouseLeftButtonDown;
        _rim.MouseMove += Rim_MouseMove;
        _rim.MouseLeftButtonUp += (_, _) => { if (_moving) _rim.ReleaseMouseCapture(); };
        _rim.LostMouseCapture += (_, _) => { _moving = false; UpdateGlass(); };
        _content.Children.Add(_rim);

        // ---- the page, in a rounded frame (concentric with the tool bar's and the card's corners)
        _web = new WebView2CompositionControl { DefaultBackgroundColor = ToDrawing(Theme.GetBrush("Card")), AllowExternalDrop = false };
        _missing = MissingRuntimePanel();
        var webArea = new Grid();
        webArea.Children.Add(_web);
        webArea.Children.Add(_missing);
        webArea.SizeChanged += (_, e) => webArea.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
        var frame = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Child = webArea };
        frame.SetResourceReference(Border.BorderBrushProperty, "B.Hair");
        frame.SetResourceReference(Border.BackgroundProperty, "B.Card");
        Grid.SetRow(frame, 1);
        _content.Children.Add(frame);

        // ---- resize edges and grip (undocked)
        var grip = new IconView { Data = GripIcon, Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 2), IsHitTestVisible = false };
        grip.SetResourceReference(IconView.IconBrushProperty, "B.Muted");
        _undockedOnly = new FrameworkElement[]
        {
            _rim,
            grip,
            Sizer(Cursors.SizeWE, HorizontalAlignment.Right, VerticalAlignment.Stretch, 7, double.NaN, true, false),
            Sizer(Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 7, false, true),
            Sizer(Cursors.SizeNWSE, HorizontalAlignment.Right, VerticalAlignment.Bottom, 18, 18, true, true),
        };
        for (int i = 1; i < _undockedOnly.Length; i++) _body.Children.Add(_undockedOnly[i]);

        MouseEnter += (_, _) => UpdateGlass();
        MouseLeave += (_, _) => { Typing?.MouseLeft(); UpdateGlass(); };
    }

    // ================================================================= opening, closing, placing
    /// <summary>Opens the panel (docked: out of the tool bar; undocked: beside <paramref name="mouse"/>, in host
    /// coordinates) and asks Google AI Mode about <paramref name="text"/>. With no text it shows AI Mode's start page,
    /// or keeps the answer that is already there. A panel that is already open stays where it is.</summary>
    public void Open(string? text, Point mouse)
    {
        var q = Clean(text);
        if (!_open)
        {
            _open = true;
            int gen = ++_gen;
            var outline = _s.LookUpUndocked ? null : _dock.Outline();
            _look = outline != null ? Look.Docked : Look.Card;
            ApplyLook();
            if (_look == Look.Docked && !Rise(outline!.Value, gen))
            {
                _look = Look.Card;      // no room above or below the tool bar: float like a card instead
                ApplyLook();
            }
            if (_look == Look.Card)
            {
                if (_s.LookUpUndocked) PlaceAtMouse(mouse); else PlaceHome();
                ShowCard();
            }
            if (!_started) { _started = true; InitWeb(); }
            HoverChanged?.Invoke();
        }
        if (q.Length > 0) Navigate(SearchUrl(q));
        else if (!_hasPage) Navigate(HomeUrl);
        FocusPage();
    }

    /// <summary>Fades the page out; docked, the tool bar then sinks back to its usual height.</summary>
    public void Close()
    {
        if (!_open) return;
        _open = false;
        _moving = false;
        int gen = ++_gen;
        bool docked = _look == Look.Docked;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(docked ? 90 : 120));
        fade.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            Visibility = Visibility.Collapsed;
            if (docked) Sink(SinkTime);
        };
        _body.BeginAnimation(OpacityProperty, fade);
        Closed?.Invoke();
        HoverChanged?.Invoke();
    }

    /// <summary>Closes at once, without animation.</summary>
    public void CloseNow()
    {
        if (!_open) return;
        _open = false;
        _moving = false;
        ++_gen;
        _body.BeginAnimation(OpacityProperty, null);
        _body.Opacity = 0;
        Visibility = Visibility.Collapsed;
        Sink(TimeSpan.Zero);
        Closed?.Invoke();
        HoverChanged?.Invoke();
    }

    /// <summary>After the window or the tool bar moved: follows the tool bar (docked) or stays inside the reading area.</summary>
    public void Reposition()
    {
        if (!_open) return;
        if (_look == Look.Docked)
        {
            if (_dock.Outline() is not Rect t) { CloseNow(); return; }   // the tool bar went away (last tab closed)
            double fits = _up ? t.Top - 4 : _host.ActualHeight - t.Bottom - 4;
            if (fits < MinRoom) { CloseNow(); return; }
            if (_room > fits)
            {
                // the window got smaller: the room shrinks with it
                _room = fits;
                SetRoom(_up ? _dock.RoomAbove : _dock.RoomBelow, _room);
                if (!_up) SetDrop(_room);
            }
            PlaceOverRoom(t);
        }
        else if (_s.LookUpUndocked) KeepInside();
        else PlaceHome();
    }

    /// <summary>The "Undocked mode" setting changed: an open panel reopens the new way, with the same page.</summary>
    public void ModeChanged()
    {
        if (!_open) return;
        CloseNow();
        Open(null, Mouse.GetPosition(_host));
    }

    /// <summary>"Tool bar transparency". Undocked the panel fades by itself; docked the window fades it with the tool bar.</summary>
    public void UpdateGlass()
    {
        if (!_open) return;
        if (_look == Look.Docked) { HoverChanged?.Invoke(); return; }
        SetGlass(!_s.ToolbarTransparency || IsMouseOver || IsMouseCaptureWithin || _moving || Typing?.Active == true ? 1.0 : GlassOpacity);
    }

    public void SetGlass(double opacity) => BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(160)));

    private void ApplyLook()
    {
        bool card = _look == Look.Card;
        _card.Visibility = card ? Visibility.Visible : Visibility.Collapsed;
        _content.Margin = card ? new Thickness(6) : new Thickness(0);
        var v = card && _s.LookUpUndocked ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in _undockedOnly) e.Visibility = v;
        RenderTransform = card ? _scale : Transform.Identity;
    }

    /// <summary>Docked: the tool bar's top edge rises (or, near the top of the window, its bottom edge drops) to make
    /// room, and the page fades in once it has. False if there isn't room either way.</summary>
    private bool Rise(Rect t, int gen)
    {
        double above = t.Top - Edge, below = _host.ActualHeight - t.Bottom - Edge;
        _up = above >= MinRoom + 120 || above >= below;
        double room = Math.Min(MaxRoom, _up ? above : below);
        if (room < MinRoom) return false;
        _room = room;
        PlaceOverRoom(t);

        // laid out (so the page can load) but not shown until the tool bar has opened
        Visibility = Visibility.Hidden;
        _body.BeginAnimation(OpacityProperty, null);
        _body.Opacity = 0;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var quick = TimeSpan.FromMilliseconds(120);
        var grow = new DoubleAnimation(room, RiseTime) { EasingFunction = ease };
        grow.Completed += (_, _) => { if (gen == _gen && _open) Reveal(); };
        if (_up)
        {
            _dock.RoomBelow.BeginAnimation(HeightProperty, new DoubleAnimation(0, quick));
            _dock.Drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, quick));
            _dock.RoomAbove.BeginAnimation(HeightProperty, grow);
        }
        else
        {
            _dock.RoomAbove.BeginAnimation(HeightProperty, new DoubleAnimation(0, quick));
            _dock.RoomBelow.BeginAnimation(HeightProperty, grow);
            _dock.Drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(room, RiseTime) { EasingFunction = ease });
        }
        return true;
    }

    private void Reveal()
    {
        Visibility = Visibility.Visible;
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        HoverChanged?.Invoke();
        FocusPage();
    }

    private void Sink(TimeSpan time)
    {
        if (time == TimeSpan.Zero)
        {
            SetRoom(_dock.RoomAbove, 0);
            SetRoom(_dock.RoomBelow, 0);
            SetDrop(0);
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _dock.RoomAbove.BeginAnimation(HeightProperty, new DoubleAnimation(0, time) { EasingFunction = ease });
        _dock.RoomBelow.BeginAnimation(HeightProperty, new DoubleAnimation(0, time) { EasingFunction = ease });
        _dock.Drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, time) { EasingFunction = ease });
    }

    private static void SetRoom(FrameworkElement room, double h)
    {
        room.BeginAnimation(HeightProperty, null);
        room.Height = h;
    }

    private void SetDrop(double y)
    {
        _dock.Drop.BeginAnimation(TranslateTransform.YProperty, null);
        _dock.Drop.Y = y;
    }

    /// <summary>Docked: the page covers the tool bar's room, inside its border and padding, with a small gap to the buttons.</summary>
    private void PlaceOverRoom(Rect t)
    {
        double i = _dock.Inset;
        Width = Math.Max(0, t.Width - 2 * i);
        Height = Math.Max(0, _room - Gap);
        double top = _up ? t.Top - _room + i : t.Bottom - i + Gap;
        Margin = new Thickness(t.Left + i, top, 0, 0);
    }

    /// <summary>Docked but without a tool bar (no PDF open): a card where the tool bar would be.</summary>
    private void PlaceHome()
    {
        double hw = _host.ActualWidth, hh = _host.ActualHeight;
        if (hw <= 0 || hh <= 0) return;
        double w = Math.Max(Math.Min(MinW, hw), Math.Min(720, hw - 2 * Edge));
        double h = Math.Max(Math.Min(MinH, hh), Math.Min(MaxRoom, hh - 2 * Edge - 18));
        Width = w;
        Height = h;
        Margin = new Thickness(Math.Max(0, (hw - w) / 2), Math.Max(0, hh - 18 - h), 0, 0);
        RenderTransformOrigin = new Point(0.5, 1);
    }

    private void ShowCard()
    {
        Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(190);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        UpdateGlass();
    }

    /// <summary>Undocked: the remembered size, beside the mouse like a right-click menu (flipped to stay inside).</summary>
    private void PlaceAtMouse(Point p)
    {
        double hw = _host.ActualWidth, hh = _host.ActualHeight;
        if (hw <= 0 || hh <= 0) return;
        double w = Math.Clamp(_s.LookUpWidth ?? 460, Math.Min(MinW, hw), Math.Max(Math.Min(MinW, hw), hw - 2 * Edge));
        double h = Math.Clamp(_s.LookUpHeight ?? 600, Math.Min(MinH, hh), Math.Max(Math.Min(MinH, hh), hh - 2 * Edge));
        double left = p.X + 12, top = p.Y + 8;
        bool flipX = left + w > hw - Edge, flipY = top + h > hh - Edge;
        if (flipX) left = p.X - 12 - w;
        if (flipY) top = p.Y - 8 - h;
        left = Math.Clamp(left, Edge, Math.Max(Edge, hw - w - Edge));
        top = Math.Clamp(top, Edge, Math.Max(Edge, hh - h - Edge));
        Width = w;
        Height = h;
        Margin = new Thickness(left, top, 0, 0);
        RenderTransformOrigin = new Point(flipX ? 1 : 0, flipY ? 1 : 0);
    }

    private void KeepInside()
    {
        double hw = _host.ActualWidth, hh = _host.ActualHeight;
        if (hw <= 0 || hh <= 0) return;
        double w = Math.Min(double.IsNaN(Width) ? ActualWidth : Width, Math.Max(MinW, hw - 2 * Edge));
        double h = Math.Min(double.IsNaN(Height) ? ActualHeight : Height, Math.Max(MinH, hh - 2 * Edge));
        Width = w;
        Height = h;
        MoveTo(Margin.Left, Margin.Top);
    }

    private void MoveTo(double left, double top)
    {
        double hw = _host.ActualWidth, hh = _host.ActualHeight;
        double w = double.IsNaN(Width) ? ActualWidth : Width, h = double.IsNaN(Height) ? ActualHeight : Height;
        Margin = new Thickness(Math.Clamp(left, Edge, Math.Max(Edge, hw - w - Edge)), Math.Clamp(top, Edge, Math.Max(Edge, hh - h - Edge)), 0, 0);
    }

    private void Rim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_look != Look.Card || !_s.LookUpUndocked) return;
        _moving = true;
        _moveStart = e.GetPosition(_host);
        _moveOrigin = Margin;
        _rim.CaptureMouse();
        e.Handled = true;
    }

    private void Rim_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving) return;
        var d = e.GetPosition(_host) - _moveStart;
        MoveTo(_moveOrigin.Left + d.X, _moveOrigin.Top + d.Y);
    }

    private Thumb Sizer(Cursor cursor, HorizontalAlignment ha, VerticalAlignment va, double w, double h, bool dx, bool dy)
    {
        var look = new FrameworkElementFactory(typeof(Border));
        look.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var t = new Thumb
        {
            Cursor = cursor,
            HorizontalAlignment = ha,
            VerticalAlignment = va,
            Width = w,
            Height = h,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = look },
        };
        t.DragDelta += (_, e) => ResizeBy(dx ? e.HorizontalChange : 0, dy ? e.VerticalChange : 0);
        t.DragCompleted += (_, _) =>
        {
            _s.LookUpWidth = Math.Round(double.IsNaN(Width) ? ActualWidth : Width);
            _s.LookUpHeight = Math.Round(double.IsNaN(Height) ? ActualHeight : Height);
            _s.Save();
            UpdateGlass();
        };
        return t;
    }

    private void ResizeBy(double dx, double dy)
    {
        double hw = _host.ActualWidth, hh = _host.ActualHeight;
        double maxW = Math.Max(MinW, hw - Margin.Left - Edge), maxH = Math.Max(MinH, hh - Margin.Top - Edge);
        if (dx != 0) Width = Math.Clamp((double.IsNaN(Width) ? ActualWidth : Width) + dx, MinW, maxW);
        if (dy != 0) Height = Math.Clamp((double.IsNaN(Height) ? ActualHeight : Height) + dy, MinH, maxH);
    }

    // ================================================================= the page
    private async void InitWeb()
    {
        try
        {
            _env ??= await CoreWebView2Environment.CreateAsync(null, DataFolder);
            await _web.EnsureCoreWebView2Async(_env);
        }
        catch (Exception)
        {
            // no WebView2 Runtime (or it failed to start): offer the browser instead
            _web.Visibility = Visibility.Collapsed;
            _missing.Visibility = Visibility.Visible;
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
            if (u.Scheme is "about" or "data") return;
            bool web = u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp;
            if (web && GoogleHost.IsMatch(u.Host)) return;
            // everything that leaves Google goes to the default browser; other schemes are ignored
            e.Cancel = true;
            if (web) OpenExternal(u.AbsoluteUri);
        };
        core.SourceChanged += (_, _) =>
        {
            try { if (core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _lastUrl = core.Source; } catch { }
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? m = null;
            try { m = e.TryGetWebMessageAsString(); } catch { }
            if (m == EscMessage) Close();
            else if (m == "rodspaette:key") Typing?.Typed();
            else if (m != null && m.StartsWith("rodspaette:chord:", StringComparison.Ordinal) && ParseChord(m) is KeyChord c) ChordPressed?.Invoke(c);
        };
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(EscScript); } catch { }
        _ready = true;
        ApplyTheme();
        var url = _pending ?? HomeUrl;
        _pending = null;
        core.Navigate(url);
        FocusPage();
    }

    private void Navigate(string url)
    {
        _hasPage = true;
        _lastUrl = url;
        if (!_ready) { _pending = url; return; }
        try { _web.CoreWebView2.Navigate(url); } catch { }
    }

    /// <summary>"rodspaette:chord:101:KeyL" → Ctrl+Shift+L (letters, digits and F-keys).</summary>
    private static KeyChord? ParseChord(string m)
    {
        var parts = m.Split(':');
        if (parts.Length != 4 || parts[2].Length != 3) return null;
        var mods = ModifierKeys.None;
        if (parts[2][0] == '1') mods |= ModifierKeys.Control;
        if (parts[2][1] == '1') mods |= ModifierKeys.Alt;
        if (parts[2][2] == '1') mods |= ModifierKeys.Shift;
        string code = parts[3];
        Key key = Key.None;
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z') key = Key.A + (code[3] - 'A');
        else if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9') key = Key.D0 + (code[5] - '0');
        else if (code.StartsWith('F') && int.TryParse(code[1..], out int f) && f is >= 1 and <= 24) key = Key.F1 + (f - 1);
        return key == Key.None ? null : new KeyChord(mods, key);
    }

    private static string SearchUrl(string q) => "https://www.google.com/search?udm=50&q=" + Uri.EscapeDataString(q);

    /// <summary>One line of plain text from a PDF selection (line breaks and runs of spaces become one space).</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text, "[\\s￾\u0002]+", " ").Trim();
        return t.Length > 1500 ? t[..1500] : t;
    }

    /// <summary>Puts the keyboard in the page, so a follow-up question can be typed straight away.</summary>
    private void FocusPage()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_open && _ready && IsVisible && _web.Visibility == Visibility.Visible) _web.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>Follows the app's theme; the page switches between Google's light and dark look.</summary>
    public void ApplyTheme()
    {
        try
        {
            _web.DefaultBackgroundColor = ToDrawing(Theme.GetBrush("Card"));
            if (_ready)
                _web.CoreWebView2.Profile.PreferredColorScheme =
                    Theme.Current == AppTheme.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }
        catch { }
    }

    private static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private StackPanel MissingRuntimePanel()
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 300, Visibility = Visibility.Collapsed };
        var title = new TextBlock { Text = "Look up can't open here", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        var msg = new TextBlock
        {
            Text = "It needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and most Windows 10 PCs.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, LineHeight = 19, Margin = new Thickness(0, 8, 0, 0),
        };
        msg.SetResourceReference(TextBlock.ForegroundProperty, "B.Text2");
        var open = new Button { Content = "Open in browser", MinWidth = 130, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        open.SetResourceReference(StyleProperty, "AccentButton");
        open.Click += (_, _) => OpenExternal(_lastUrl ?? HomeUrl);
        var install = new Button { Content = "Get WebView2", MinWidth = 130, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        install.SetResourceReference(StyleProperty, "PlainButton");
        install.Click += (_, _) => OpenExternal(GetRuntimeUrl);
        p.Children.Add(title);
        p.Children.Add(msg);
        p.Children.Add(open);
        p.Children.Add(install);
        return p;
    }

    private static System.Drawing.Color ToDrawing(Brush b)
    {
        var c = b is SolidColorBrush sb ? sb.Color : Colors.White;
        return System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
    }
}
