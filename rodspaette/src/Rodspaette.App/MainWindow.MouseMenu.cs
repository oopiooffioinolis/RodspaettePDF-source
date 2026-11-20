using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rodspaette.App;

/// <summary>The right-drag function dial ("mouse menu" in the code) and the commands it (and Settings) can choose from.</summary>
public partial class MainWindow
{
    /// <summary>Up, right, down, left.</summary>
    public static readonly string[] DefaultMouseMenu = { "highlight", "underline", "pen", "note" };

    public sealed record MouseAction(string Id, string Label, string Icon, Action Run);

    private const double RightDragThreshold = 10;
    private RadialMenu? _radial;
    private Point _rStart;
    private bool _rPending;

    /// <summary>Every command in the app that can sit in the mouse menu.</summary>
    public List<MouseAction> MouseActions() => new()
    {
        new("select", "Select text", Icons.ToolSelect, () => SelectTool(Tool.Select)),
        new("highlight", "Highlight", Icons.ToolHighlight, () => MarkupOrTool(Tool.Highlight)),
        new("underline", "Underline", Icons.ToolUnderline, () => MarkupOrTool(Tool.Underline)),
        new("strike", "Strikethrough", Icons.ToolStrike, () => MarkupOrTool(Tool.Strike)),
        new("pen", "Freehand", Icons.ToolPen, () => SelectTool(Tool.Pen)),
        new("note", "Sticky note", Icons.ToolNote, () => SelectTool(Tool.Note)),
        new("text", "Text box", Icons.ToolText, () => SelectTool(Tool.Text)),
        new("undo", "Undo", Icons.Undo, async () => { if (_active != null && !await _active.Viewer.Undo()) ShowToast("Nothing to undo"); }),
        new("copy", "Copy", Icons.Copy, () => { if (_active?.Viewer.CopySelection() == true) ShowToast("Copied"); }),
        new("bookmark", "Bookmark page", Icons.Bookmark, async () => await ToggleBookmark()),
        new("search", "Search", Icons.Search, OpenSearch),
        new("lookup", "Look up", Icons.LookUp, LookUp),
        new("contents", "Contents panel", Icons.Sidebar, () => { if (!LeftDocked) { _leftPeek = true; _tabsPeek = false; ApplyChrome(); } }),
        new("tabs", "Tabs panel", Icons.Tabs, () => { if (!TabsDocked) { _tabsPeek = true; _leftPeek = false; ApplyChrome(); } }),
        new("panels", "Retract panels", Icons.Sidebar, TogglePanels),
        new("fullscreen", "Full screen", Icons.Focus, () => ToggleFullScreen(!_wantFullScreen)),
        new("zoomin", "Zoom in", Icons.Plus, () => _active?.Viewer.ZoomIn()),
        new("zoomout", "Zoom out", Icons.Minus, () => _active?.Viewer.ZoomOut()),
        new("fitwidth", "Fit width", Icons.FitWidth, () => _active?.Viewer.FitToWidth()),
        new("toolbarhome", "Toolbar home", Icons.Home, ResetToolbar),
        new("theme", "Next theme", Icons.HalfFill, NextTheme),
        new("open", "Open file", Icons.File, ShowOpenDialog),
        new("save", "Save", Icons.Save, async () => { if (_active != null) await Save(_active, false); }),
        new("saveas", "Save As", Icons.SaveAs, async () => { if (_active != null) await Save(_active, true); }),
        new("print", "Print", Icons.Print, PrintActive),
        new("nexttab", "Next tab", Icons.ChevronRight, () => CycleTabs(1)),
        new("closetab", "Close tab", Icons.Close, async () => { if (_active != null) await CloseTab(_active); }),
        new("request", "Update request", Icons.Report, ReportError),
        new("updateapp", "Update app", Icons.Refresh, UpdateApp),
        new("none", "Nothing", Icons.Minus, () => { }),
    };

    /// <summary>Look up the selected text (or ask anything) with Google AI Mode in a small window.</summary>
    private void LookUp() => OpenLookUp(_active?.Viewer.SelectedText());

    /// <summary>With text selected, a markup command marks it right away; otherwise it picks the tool.</summary>
    private void MarkupOrTool(Tool t)
    {
        var v = _active?.Viewer;
        if (v != null && v.HasSelection) v.ApplyMarkupToSelection(t);
        else SelectTool(t);
    }

    private void NextTheme()
    {
        switch (Theme.Current)
        {
            case AppTheme.Light: ThemeDark.IsChecked = true; break;
            case AppTheme.Dark: ThemeGreen.IsChecked = true; break;
            default: ThemeLight.IsChecked = true; break;
        }
    }

    /// <summary>The four commands in the mouse menu: up, right, down, left.</summary>
    public string[] MouseMenuSlots()
    {
        var ids = MouseActions().Select(a => a.Id).ToHashSet();
        var slots = _s.MouseMenu is { Length: 4 } m ? m.ToArray() : DefaultMouseMenu.ToArray();
        for (int i = 0; i < 4; i++) if (!ids.Contains(slots[i])) slots[i] = DefaultMouseMenu[i];
        return slots;
    }

    private void InitMouseMenu()
    {
        _radial = new RadialMenu();
        Panel.SetZIndex(_radial, 60);
        ViewerHost.Children.Add(_radial);
        ViewerHost.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (_active == null || !_active.Viewer.IsMouseOver || _radial.IsOpen) return;
            _rStart = e.GetPosition(ViewerHost);
            _rPending = true;
        };
        ViewerHost.PreviewMouseMove += (_, e) =>
        {
            if (_radial.IsOpen) { _radial.Track(e.GetPosition(ViewerHost)); return; }
            if (!_rPending) return;
            if (e.RightButton != MouseButtonState.Pressed) { _rPending = false; return; }
            if ((e.GetPosition(ViewerHost) - _rStart).Length < RightDragThreshold) return;
            // dragging with the right button held: open the mouse menu where the button went down
            _rPending = false;
            PopupMenu.Current?.Close();
            var all = MouseActions().ToDictionary(a => a.Id);
            var slots = MouseMenuSlots().Select(id => (all[id].Label, all[id].Icon)).ToList();
            _radial.Show(_rStart, slots);
            _radial.Track(e.GetPosition(ViewerHost));
            Mouse.Capture(_radial);
            e.Handled = true;
        };
        ViewerHost.PreviewMouseRightButtonUp += (_, e) =>
        {
            _rPending = false;
            if (!_radial.IsOpen) return;   // a plain right click: the normal menu opens
            e.Handled = true;
            int sel = _radial.Selected;
            CloseRadial();
            if (sel < 0) return;
            var id = MouseMenuSlots()[sel];
            var action = MouseActions().FirstOrDefault(a => a.Id == id);
            if (action != null) Dispatcher.InvokeAsync(action.Run);
        };
        _radial.LostMouseCapture += (_, _) => { if (_radial.IsOpen && Mouse.RightButton != MouseButtonState.Pressed) CloseRadial(); };
    }

    private void CloseRadial()
    {
        if (_radial == null) return;
        _radial.Hide();
        if (Mouse.Captured == _radial) Mouse.Capture(null);
    }

    // ---------------------------------------------------- settings
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsRestoreSwitch.IsChecked = _s.RestoreSession;
        SettingsGlassSwitch.IsChecked = _s.ToolbarTransparency;
        SettingsUndockSwitch.IsChecked = _s.LookUpUndocked;
        SettingsVersion.Text = "Version " + AppInfo.Version;
        SettingsUpdateItem.IsEnabled = !_updating;
        SettingsUpdateText.Text = _updating ? "Updating…" : "Update app";
        SettingsMenu.IsOpen = !SettingsMenu.IsOpen;
    }

    private void MouseMenuSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsMenu.IsOpen = false;
        SettingsDialog.Show(this);
    }

    private void MouseBehaviour_Click(object sender, RoutedEventArgs e)
    {
        SettingsMenu.IsOpen = false;
        MouseBehaviourDialog.Show(this, _s);
    }

    private void ShortcutsSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsMenu.IsOpen = false;
        ShortcutsDialog.Show(this);
        UpdateBookmarkState();
    }

    private void SettingsRestore_Click(object sender, RoutedEventArgs e) => RestoreSession = SettingsRestoreSwitch.IsChecked == true;

    private void SettingsGlass_Click(object sender, RoutedEventArgs e)
    {
        _s.ToolbarTransparency = SettingsGlassSwitch.IsChecked == true;
        _s.Save();
        UpdateToolbarGlass();
        _lookUp?.UpdateGlass();
    }

    private void SettingsUndock_Click(object sender, RoutedEventArgs e)
    {
        _s.LookUpUndocked = SettingsUndockSwitch.IsChecked == true;
        _s.Save();
        _lookUp?.ModeChanged();
    }

    /// <summary>Right-click on the start page (no PDF open): only what applies there.</summary>
    private void InitHomeMenu()
    {
        ViewerHost.MouseRightButtonUp += (_, e) =>
        {
            if (_active != null || e.Handled || _lookUp?.IsMouseOver == true) return;
            e.Handled = true;
            var m = new PopupMenu();
            m.Item("Open…", Shortcuts.Text("open"), ShowOpenDialog, Icons.File);
            m.Separator();
            m.Item("Update request…", null, ReportError, Icons.Report);
            m.Open(ViewerHost);
        };
    }

    public void SetMouseMenuSlot(int slot, string id)
    {
        var slots = MouseMenuSlots();
        slots[slot] = id;
        _s.MouseMenu = slots;
        _s.Save();
    }

    public void ResetMouseMenu()
    {
        _s.MouseMenu = null;
        _s.Save();
    }
}
