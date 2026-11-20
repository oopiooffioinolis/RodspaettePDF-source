using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rodspaette.App;

/// <summary>A key with its modifiers, like Ctrl+Shift+S. <see cref="Key.None"/> means "no shortcut".</summary>
public readonly record struct KeyChord(ModifierKeys Mods, Key Key)
{
    public bool IsNone => Key == Key.None;

    /// <summary>With Ctrl or Alt, or a function key: safe to run while the keyboard is in a text box.</summary>
    public bool WorksInText => (Mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 || Key is >= Key.F1 and <= Key.F24;

    /// <summary>The number pad's digits, plus and minus count as the main keyboard's.</summary>
    public static Key Normalize(Key k) => k switch
    {
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        >= Key.NumPad0 and <= Key.NumPad9 => Key.D0 + (k - Key.NumPad0),
        _ => k,
    };

    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed;

    /// <summary>How it's stored in settings: modifiers and the key's name, e.g. "Ctrl+Shift+OemPlus" ("" = none).</summary>
    public string Serialize()
    {
        if (IsNone) return "";
        var sb = new StringBuilder();
        if ((Mods & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((Mods & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((Mods & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        if ((Mods & ModifierKeys.Windows) != 0) sb.Append("Win+");
        return sb.Append(Key).ToString();
    }

    public static KeyChord Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !Enum.TryParse<Key>(parts[^1], out var key)) return default;
        var mods = ModifierKeys.None;
        foreach (var p in parts[..^1])
            mods |= p switch { "Ctrl" => ModifierKeys.Control, "Alt" => ModifierKeys.Alt, "Shift" => ModifierKeys.Shift, "Win" => ModifierKeys.Windows, _ => ModifierKeys.None };
        return new KeyChord(mods, Normalize(key));
    }

    /// <summary>As people read it, e.g. "Ctrl+Shift+S", "Ctrl++", "Shift+F3", "Del".</summary>
    public override string ToString()
    {
        if (IsNone) return "";
        var sb = new StringBuilder();
        if ((Mods & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((Mods & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((Mods & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        if ((Mods & ModifierKeys.Windows) != 0) sb.Append("Win+");
        return sb.Append(KeyName(Key)).ToString();
    }

    private static string KeyName(Key k)
    {
        switch (k)
        {
            case >= Key.A and <= Key.Z: return k.ToString();
            case >= Key.D0 and <= Key.D9: return ((char)('0' + (k - Key.D0))).ToString();
            case >= Key.F1 and <= Key.F24: return k.ToString();
            case Key.OemPlus: return "+";
            case Key.OemMinus: return "-";
            case Key.OemComma: return ",";
            case Key.OemPeriod: return ".";
            case Key.Delete: return "Del";
            case Key.Insert: return "Ins";
            case Key.Back: return "Backspace";
            case Key.Return: return "Enter";
            case Key.Escape: return "Esc";
            case Key.PageUp: return "PgUp";
            case Key.PageDown: return "PgDn";
            case Key.Left: return "←";
            case Key.Right: return "→";
            case Key.Up: return "↑";
            case Key.Down: return "↓";
            case Key.Space: return "Space";
            case Key.Tab: return "Tab";
            case Key.Home: return "Home";
            case Key.End: return "End";
        }
        // other keys: the character printed on the key in the current keyboard layout
        try
        {
            uint ch = MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(k), 2) & 0x7FFF;
            if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
        }
        catch { }
        return k.ToString();
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}

/// <summary>One command that can have a keyboard shortcut.</summary>
/// <param name="PageOnly">Only while the keyboard isn't in a text box (copy, undo, zoom…), so typing works as usual there.</param>
public sealed record ShortcutCommand(string Id, string Label, string Group, KeyChord Default, bool PageOnly = false);

/// <summary>
/// The app's keyboard shortcuts: the commands, their default keys, and the user's changes from Settings → Shortcuts…
/// (kept in settings.json as overrides of the defaults). Esc is not a shortcut: it always cancels or closes.
/// </summary>
public static class Shortcuts
{
    private static KeyChord C(Key k, ModifierKeys m = ModifierKeys.Control) => new(m, k);
    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
    private static readonly KeyChord None = default;

    public static readonly IReadOnlyList<ShortcutCommand> All = new ShortcutCommand[]
    {
        new("open", "Open…", "Files and tabs", C(Key.O)),
        new("save", "Save", "Files and tabs", C(Key.S)),
        new("saveas", "Save As…", "Files and tabs", C(Key.S, CtrlShift)),
        new("print", "Print…", "Files and tabs", C(Key.P)),
        new("closetab", "Close tab", "Files and tabs", C(Key.W)),
        new("nexttab", "Next tab (goes round all tabs)", "Files and tabs", C(Key.Tab)),
        new("prevtab", "Previous tab", "Files and tabs", C(Key.Tab, CtrlShift)),
        new("lasttab", "Last-used tab (switches between the two most recent)", "Files and tabs", C(Key.Q)),
        new("pagenumbers", "Page numbers…", "Files and tabs", None),

        new("search", "Search", "Reading", C(Key.F)),
        new("nexthit", "Next search result", "Reading", new KeyChord(ModifierKeys.None, Key.F3)),
        new("prevhit", "Previous search result", "Reading", new KeyChord(ModifierKeys.Shift, Key.F3)),
        new("lookup", "Look up", "Reading", C(Key.L)),
        new("bookmark", "Bookmark page", "Reading", C(Key.D)),
        new("panels", "Show or hide the side panels", "Reading", C(Key.H)),
        new("zoomin", "Zoom in", "Reading", C(Key.OemPlus), PageOnly: true),
        new("zoomout", "Zoom out", "Reading", C(Key.OemMinus), PageOnly: true),
        new("fitwidth", "Fit width", "Reading", C(Key.D0), PageOnly: true),
        new("fullscreen", "Full screen", "Reading", None),
        new("home", "Home page", "Reading", None),

        new("tool-select", "Select text", "Tools", None, PageOnly: true),
        new("tool-highlight", "Highlight", "Tools", None, PageOnly: true),
        new("tool-underline", "Underline", "Tools", None, PageOnly: true),
        new("tool-strike", "Strikethrough", "Tools", None, PageOnly: true),
        new("tool-pen", "Pen", "Tools", None, PageOnly: true),
        new("tool-note", "Note", "Tools", None, PageOnly: true),
        new("tool-text", "Text box", "Tools", None, PageOnly: true),

        new("undo", "Undo", "Editing", C(Key.Z), PageOnly: true),
        new("copy", "Copy", "Editing", C(Key.C), PageOnly: true),
        new("delete", "Delete the selected markup", "Editing", new KeyChord(ModifierKeys.None, Key.Delete), PageOnly: true),
    };

    private static readonly Dictionary<string, KeyChord> Current = new();
    private static AppSettings? _s;

    /// <summary>Raised after any shortcut changed.</summary>
    public static event Action? Changed;

    public static void Load(AppSettings s)
    {
        _s = s;
        Current.Clear();
        foreach (var c in All)
            Current[c.Id] = s.Shortcuts != null && s.Shortcuts.TryGetValue(c.Id, out var v) ? KeyChord.Parse(v) : c.Default;
    }

    public static KeyChord Get(string id) => Current.TryGetValue(id, out var k) ? k : default;

    /// <summary>The shortcut as shown in menus and tool tips, or null when the command has none.</summary>
    public static string? Text(string id) { var k = Get(id); return k.IsNone ? null : k.ToString(); }

    public static ShortcutCommand? Find(KeyChord chord)
    {
        if (chord.IsNone) return null;
        foreach (var c in All) if (Current.TryGetValue(c.Id, out var k) && k == chord) return c;
        return null;
    }

    public static bool IsDefault(string id) => All.First(c => c.Id == id).Default == Get(id);

    /// <summary>Gives <paramref name="id"/> this shortcut. A command that had it loses it; that command is returned.</summary>
    public static ShortcutCommand? Set(string id, KeyChord chord)
    {
        ShortcutCommand? taken = null;
        if (!chord.IsNone)
            foreach (var c in All)
                if (c.Id != id && Get(c.Id) == chord) { Current[c.Id] = default; taken = c; }
        Current[id] = chord;
        Save();
        return taken;
    }

    public static void Reset(string id)
    {
        var def = All.First(c => c.Id == id).Default;
        Set(id, def);
    }

    public static void ResetAll()
    {
        foreach (var c in All) Current[c.Id] = c.Default;
        Save();
    }

    private static void Save()
    {
        if (_s != null)
        {
            var o = new Dictionary<string, string>();
            foreach (var c in All) { var k = Get(c.Id); if (k != c.Default) o[c.Id] = k.Serialize(); }
            _s.Shortcuts = o.Count > 0 ? o : null;
            _s.Save();
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Keeps shortcut hints in menus and tool tips in step with Settings → Shortcuts…:
/// <c>local:Hint.Shortcut="save"</c> on a TextBlock shows the key (and hides it when there is none);
/// <c>local:Hint.Tip="undo"</c> on a control adds the key to its tool tip, e.g. "Undo (Ctrl+Z)".
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty ShortcutProperty =
        DependencyProperty.RegisterAttached("Shortcut", typeof(string), typeof(Hint), new PropertyMetadata(null, (d, _) => Track(d)));
    public static string? GetShortcut(DependencyObject o) => (string?)o.GetValue(ShortcutProperty);
    public static void SetShortcut(DependencyObject o, string? v) => o.SetValue(ShortcutProperty, v);

    public static readonly DependencyProperty TipProperty =
        DependencyProperty.RegisterAttached("Tip", typeof(string), typeof(Hint), new PropertyMetadata(null, (d, _) => Track(d)));
    public static string? GetTip(DependencyObject o) => (string?)o.GetValue(TipProperty);
    public static void SetTip(DependencyObject o, string? v) => o.SetValue(TipProperty, v);

    private static readonly DependencyProperty BaseTipProperty =
        DependencyProperty.RegisterAttached("BaseTip", typeof(string), typeof(Hint), new PropertyMetadata(null));

    private static readonly List<WeakReference<DependencyObject>> Items = new();

    static Hint()
    {
        Shortcuts.Changed += () =>
        {
            Items.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in Items.ToArray()) if (w.TryGetTarget(out var d)) Apply(d);
        };
    }

    private static void Track(DependencyObject d)
    {
        if (!Items.Any(w => w.TryGetTarget(out var x) && ReferenceEquals(x, d)))
        {
            Items.Add(new WeakReference<DependencyObject>(d));
            // XAML may set the tool tip after this property: look again once the element is shown
            if (d is FrameworkElement fe && !fe.IsLoaded) fe.Loaded += (_, _) => Apply(fe);
        }
        Apply(d);
    }

    private static void Apply(DependencyObject d)
    {
        if (GetShortcut(d) is string id && d is TextBlock tb)
        {
            var t = Shortcuts.Text(id);
            tb.Text = t ?? "";
            tb.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (GetTip(d) is string tipId && d is FrameworkElement fe)
        {
            if (fe.GetValue(BaseTipProperty) is not string baseTip)
            {
                if (fe.ToolTip is not string first) return;
                baseTip = first;
                fe.SetValue(BaseTipProperty, baseTip);
            }
            var t = Shortcuts.Text(tipId);
            fe.ToolTip = t == null ? baseTip : $"{baseTip} ({t})";
        }
    }
}
