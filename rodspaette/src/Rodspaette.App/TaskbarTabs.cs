using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Rodspaette.Core;

namespace Rodspaette.App;

/// <summary>
/// Gives every open tab its own thumbnail in the Windows taskbar hover preview (the same mechanism
/// browsers used for per-tab previews). Each tab has a hidden proxy window registered with ITaskbarList3.
/// The main window stays the only real window, so the taskbar shows one icon.
/// </summary>
public sealed class TaskbarTabs : IDisposable
{
    private readonly Window _main;
    private IntPtr _mainHwnd;
    private Win32.ITaskbarList4? _taskbar;
    private readonly Dictionary<DocumentTab, HwndSource> _proxies = new();
    private IntPtr _iconSmall, _iconLarge;

    public event Action<DocumentTab>? ActivateRequested;
    public event Action<DocumentTab>? CloseRequested;

    public TaskbarTabs(Window main)
    {
        _main = main;
    }

    public void Attach()
    {
        _mainHwnd = new WindowInteropHelper(_main).Handle;
        _taskbar ??= Win32.CreateTaskbarList();
        if (_iconSmall == IntPtr.Zero)
        {
            try { Win32.ExtractIconEx(Environment.ProcessPath ?? "", 0, out _iconLarge, out _iconSmall, 1); } catch { }
        }
    }

    /// <summary>Called when Explorer (re)creates the taskbar button: register everything again.</summary>
    public void Reregister(IEnumerable<DocumentTab> tabs, DocumentTab? active)
    {
        _taskbar = Win32.CreateTaskbarList();
        foreach (var t in tabs)
        {
            if (_proxies.TryGetValue(t, out var src))
                _taskbar?.RegisterTab(src.Handle, _mainHwnd);
        }
        foreach (var t in tabs)
            if (_proxies.TryGetValue(t, out var src)) _taskbar?.SetTabOrder(src.Handle, IntPtr.Zero);
        if (active != null) SetActive(active);
    }

    public void Add(DocumentTab tab)
    {
        if (_taskbar == null || _mainHwnd == IntPtr.Zero || _proxies.ContainsKey(tab)) return;
        var p = new HwndSourceParameters(tab.Name + " — Rødspætte PDF")
        {
            WindowStyle = Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU,
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1,
        };
        var src = new HwndSource(p);
        src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => Hook(tab, hwnd, msg, wParam, lParam, ref handled));
        IntPtr h = src.Handle;
        tab.ProxyHwnd = h;
        Win32.SetDwmBool(h, Win32.DWMWA_FORCE_ICONIC_REPRESENTATION, true);
        Win32.SetDwmBool(h, Win32.DWMWA_HAS_ICONIC_BITMAP, true);
        if (_iconSmall != IntPtr.Zero) Win32.SendMessage(h, Win32.WM_SETICON, IntPtr.Zero, _iconSmall);
        if (_iconLarge != IntPtr.Zero) Win32.SendMessage(h, Win32.WM_SETICON, new IntPtr(1), _iconLarge);
        _proxies[tab] = src;
        _taskbar.RegisterTab(h, _mainHwnd);
        _taskbar.SetTabOrder(h, IntPtr.Zero);
    }

    /// <summary>Puts the taskbar previews in the same order as the tabs list.</summary>
    public void Reorder(IEnumerable<DocumentTab> tabs)
    {
        foreach (var t in tabs)
            if (_proxies.TryGetValue(t, out var src)) _taskbar?.SetTabOrder(src.Handle, IntPtr.Zero);
    }

    public void Remove(DocumentTab tab)
    {
        if (!_proxies.TryGetValue(tab, out var src)) return;
        _taskbar?.UnregisterTab(src.Handle);
        _proxies.Remove(tab);
        src.Dispose();
        tab.ProxyHwnd = IntPtr.Zero;
    }

    public void SetActive(DocumentTab tab)
    {
        if (_proxies.TryGetValue(tab, out var src)) _taskbar?.SetTabActive(src.Handle, _mainHwnd, 0);
    }

    public void Rename(DocumentTab tab)
    {
        if (_proxies.TryGetValue(tab, out var src)) Win32.SetWindowText(src.Handle, tab.Name + " — Rødspætte PDF");
    }

    public void Invalidate(DocumentTab tab)
    {
        if (_proxies.TryGetValue(tab, out var src)) Win32.DwmInvalidateIconicBitmaps(src.Handle);
    }

    private IntPtr Hook(DocumentTab tab, IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_ACTIVATE:
                if ((wParam.ToInt64() & 0xFFFF) != Win32.WA_INACTIVE)
                {
                    handled = true;
                    _main.Dispatcher.InvokeAsync(() => ActivateRequested?.Invoke(tab));
                    return IntPtr.Zero;
                }
                break;
            case Win32.WM_SYSCOMMAND:
                if ((wParam.ToInt64() & 0xFFF0) == Win32.SC_CLOSE)
                {
                    handled = true;
                    _main.Dispatcher.InvokeAsync(() => CloseRequested?.Invoke(tab));
                    return IntPtr.Zero;
                }
                break;
            case Win32.WM_CLOSE:
                handled = true;
                _main.Dispatcher.InvokeAsync(() => CloseRequested?.Invoke(tab));
                return IntPtr.Zero;
            case Win32.WM_DWMSENDICONICTHUMBNAIL:
            {
                long lp = lParam.ToInt64();
                int maxW = (int)((lp >> 16) & 0xFFFF), maxH = (int)(lp & 0xFFFF);
                handled = true;
                _ = SendThumbnail(tab, hwnd, maxW, maxH, live: false);
                return IntPtr.Zero;
            }
            case Win32.WM_DWMSENDICONICLIVEPREVIEWBITMAP:
            {
                handled = true;
                var helper = new WindowInteropHelper(_main);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_main);
                int w = (int)(_main.ActualWidth * dpi.DpiScaleX), h = (int)(_main.ActualHeight * dpi.DpiScaleY);
                _ = SendThumbnail(tab, hwnd, Math.Max(1, w), Math.Max(1, h), live: true);
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Renders the tab's current page to fit (maxW × maxH) and hands it to DWM.</summary>
    private async Task SendThumbnail(DocumentTab tab, IntPtr hwnd, int maxW, int maxH, bool live)
    {
        try
        {
            var doc = tab.Doc;
            int page = Math.Clamp(tab.CurrentPage, 0, Math.Max(0, doc.PageCount - 1));
            var size = doc.GetPageSize(page);
            double scale = Math.Min(maxW / size.Width, maxH / size.Height);
            int pw = Math.Max(1, (int)(size.Width * scale)), ph = Math.Max(1, (int)(size.Height * scale));
            var theme = Theme.Current;
            bool tint = Theme.TintPages;
            var px = await PdfWorker.Shared.Run(() =>
            {
                var b = doc.Render(page, scale, 0, 0, pw, ph);
                Theme.TintPixels(b, theme, tint);
                return b;
            });
            byte[] outPx;
            int ow, oh;
            if (live)
            {
                // centre the page on a background the size of the window
                ow = maxW; oh = maxH;
                outPx = new byte[ow * oh * 4];
                var bg = Theme.GetColor("Viewer");
                for (int i = 0; i < outPx.Length; i += 4) { outPx[i] = bg.B; outPx[i + 1] = bg.G; outPx[i + 2] = bg.R; outPx[i + 3] = 255; }
                int ox = (ow - pw) / 2, oy = (oh - ph) / 2;
                for (int y = 0; y < ph; y++)
                    Buffer.BlockCopy(px, y * pw * 4, outPx, ((oy + y) * ow + ox) * 4, pw * 4);
            }
            else { outPx = px; ow = pw; oh = ph; }
            IntPtr hbmp = Win32.CreateBitmap(outPx, ow, oh);
            if (hbmp == IntPtr.Zero) return;
            if (live) Win32.DwmSetIconicLivePreviewBitmap(hwnd, hbmp, IntPtr.Zero, 0);
            else Win32.DwmSetIconicThumbnail(hwnd, hbmp, 0);
            Win32.DeleteObject(hbmp);
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var t in new List<DocumentTab>(_proxies.Keys)) Remove(t);
        if (_iconSmall != IntPtr.Zero) Win32.DestroyIcon(_iconSmall);
        if (_iconLarge != IntPtr.Zero) Win32.DestroyIcon(_iconLarge);
    }
}
