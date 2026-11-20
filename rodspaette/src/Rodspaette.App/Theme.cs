using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Rodspaette.Core;

namespace Rodspaette.App;

public enum AppTheme { Light, Dark, Green }

/// <summary>Swaps the brush resources used everywhere via DynamicResource.</summary>
public static class Theme
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;
    public static bool TintPages { get; set; } = true;

    public static readonly RgbColor GreenPaper = RgbColor.FromHex("C7EDCC");

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Win"] = "#F5F5F7", ["Bar"] = "#F6F6F8", ["Hair"] = "#14000000", ["Text"] = "#1D1D1F", ["Text2"] = "#3A3A3C",
        ["Muted"] = "#6E6E73", ["Viewer"] = "#E8E8EB", ["Panel"] = "#FAFAFC", ["Card"] = "#FFFFFF", ["Glass"] = "#F5FCFCFD",
        ["Hover"] = "#0F000000", ["Seg"] = "#0D000000", ["SegOn"] = "#FFFFFF", ["Accent"] = "#0066CC", ["AccentBtn"] = "#0066CC",
        ["AccentSoft"] = "#1F0066CC", ["SbBg"] = "#FAFAFAFC", ["MenuBg"] = "#FCFCFD", ["Edge"] = "#24000000", ["Field"] = "#FFFFFF",
        ["FieldBorder"] = "#1F000000", ["Overlay"] = "#2E141416", ["PageShadow"] = "#1A000000", ["Selection"] = "#4D0066CC",
        ["ScrollThumb"] = "#40000000", ["SearchHit"] = "#55FF9500", ["SearchCurrent"] = "#B3FF9500", ["Danger"] = "#C93400",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Win"] = "#1C1C1E", ["Bar"] = "#262628", ["Hair"] = "#17FFFFFF", ["Text"] = "#F5F5F7", ["Text2"] = "#D1D1D6",
        ["Muted"] = "#A1A1A6", ["Viewer"] = "#111113", ["Panel"] = "#1E1E20", ["Card"] = "#2C2C2E", ["Glass"] = "#F52C2C2E",
        ["Hover"] = "#14FFFFFF", ["Seg"] = "#14FFFFFF", ["SegOn"] = "#4A4A4E", ["Accent"] = "#6AB0FF", ["AccentBtn"] = "#0B63C9",
        ["AccentSoft"] = "#296AB0FF", ["SbBg"] = "#FA242426", ["MenuBg"] = "#2C2C2E", ["Edge"] = "#2EFFFFFF", ["Field"] = "#1C1C1E",
        ["FieldBorder"] = "#24FFFFFF", ["Overlay"] = "#73000000", ["PageShadow"] = "#66000000", ["Selection"] = "#596AB0FF",
        ["ScrollThumb"] = "#50FFFFFF", ["SearchHit"] = "#55FF9F0A", ["SearchCurrent"] = "#B3FF9F0A", ["Danger"] = "#FF8A4C",
    };

    private static readonly Dictionary<string, string> GreenMode = new()
    {
        ["Win"] = "#E4EEE4", ["Bar"] = "#E0ECE0", ["Hair"] = "#1C143C1E", ["Text"] = "#1C2A1F", ["Text2"] = "#2F4034",
        ["Muted"] = "#536457", ["Viewer"] = "#D2E1D3", ["Panel"] = "#E4EFE4", ["Card"] = "#EFF7EF", ["Glass"] = "#F5EAF4EA",
        ["Hover"] = "#12143C1E", ["Seg"] = "#12143C1E", ["SegOn"] = "#F5FAF5", ["Accent"] = "#1E6A39", ["AccentBtn"] = "#1E6A39",
        ["AccentSoft"] = "#211E6A39", ["SbBg"] = "#FAE8F2E8", ["MenuBg"] = "#EEF6EE", ["Edge"] = "#2E143C1E", ["Field"] = "#F5FAF5",
        ["FieldBorder"] = "#29143C1E", ["Overlay"] = "#33142819", ["PageShadow"] = "#22143C1E", ["Selection"] = "#4D1E6A39",
        ["ScrollThumb"] = "#40143C1E", ["SearchHit"] = "#55FF9500", ["SearchCurrent"] = "#B3FF9500", ["Danger"] = "#B5400C",
    };

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var map = theme switch { AppTheme.Dark => Dark, AppTheme.Green => GreenMode, _ => Light };
        var res = Application.Current.Resources;
        foreach (var kv in map)
        {
            var c = (Color)ColorConverter.ConvertFromString(kv.Value);
            var b = new SolidColorBrush(c);
            b.Freeze();
            res["B." + kv.Key] = b;
            res["C." + kv.Key] = c;
        }
    }

    public static Brush GetBrush(string key) => (Brush)Application.Current.Resources["B." + key];
    public static Color GetColor(string key) => (Color)Application.Current.Resources["C." + key];

    /// <summary>Applies the page tint for the current theme to rendered BGRA pixels (runs on the PDF worker).</summary>
    public static void TintPixels(byte[] bgra, AppTheme theme, bool tint)
    {
        if (!tint) return;
        if (theme == AppTheme.Dark) PageTint.ApplyDark(bgra);
        else if (theme == AppTheme.Green) PageTint.ApplyMultiply(bgra, GreenPaper);
    }

    public static Color PaperColor(AppTheme theme, bool tint) =>
        !tint ? Colors.White :
        theme == AppTheme.Dark ? System.Windows.Media.Color.FromRgb(38, 38, 38) :
        theme == AppTheme.Green ? System.Windows.Media.Color.FromRgb(GreenPaper.R, GreenPaper.G, GreenPaper.B) :
        Colors.White;
}
