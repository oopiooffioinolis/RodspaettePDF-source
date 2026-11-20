using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodspaette.App;

public sealed class RecentFile
{
    public string Path { get; set; } = "";
    public int Page { get; set; }
    public DateTime Opened { get; set; }
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public bool TintPages { get; set; } = true;
    public string? SourceFolder { get; set; }
    public bool AutoSave { get; set; }
    public string[]? MouseMenu { get; set; }
    public bool RestoreSession { get; set; } = true;   // open where I left off
    public List<string> SessionFiles { get; set; } = new();
    public string? SessionActive { get; set; }   // up, right, down, left; null = defaults
    public List<RecentFile> Recent { get; set; } = new();
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 1280;
    public double WinHeight { get; set; } = 860;
    public bool ToolbarTransparency { get; set; }   // the tool bar and Look up fade a little while the mouse is away
    public bool LookUpUndocked { get; set; }        // Look up opens beside the mouse and can be moved and resized
    public double? LookUpWidth { get; set; }        // its size in undocked mode (null = 460 x 600)
    public double? LookUpHeight { get; set; }
    public double ScrollSpeed { get; set; } = 1.0;   // Settings → Mouse behaviour…: scroll sensitivity (1 = Windows' usual distance)
    public Dictionary<string, string>? Shortcuts { get; set; }   // Settings → Shortcuts…: changed keys only ("" = none)
    public bool Maximized { get; set; } = true;
    public bool TabsPinned { get; set; }
    public bool LeftPanelVisible { get; set; }   // left panel pinned
    public int LayoutVersion { get; set; } = 2;
    public double ToolbarX { get; set; }
    public double ToolbarY { get; set; }
    public string LeftView { get; set; } = "contents";
    public bool AskedLastActiveClick { get; set; }
    public Dictionary<string, string> ToolColors { get; set; } = new()
    {
        ["highlight"] = "#FFD60A",
        ["underline"] = "#D9480F",
        ["strike"] = "#E5341F",
        ["pen"] = "#E5341F",
        ["note"] = "#FFD60A",
        ["text"] = "#1D1D1F",
    };
    public double PenWidth { get; set; } = 2;
    public double TextSize { get; set; } = 12;
    public bool FitWidth { get; set; } = true;
    public double Zoom { get; set; } = 1.0;

    [JsonIgnore]
    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rødspætte PDF");
    [JsonIgnore]
    private static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s != null)
                {
                    var defaults = new AppSettings().ToolColors;
                    foreach (var kv in defaults) if (!s.ToolColors.ContainsKey(kv.Key)) s.ToolColors[kv.Key] = kv.Value;
                    if (s.LayoutVersion < 2) { s.LeftPanelVisible = false; s.TabsPinned = false; s.LayoutVersion = 2; }
                    return s;
                }
            }
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, true);
        }
        catch { /* never crash on settings */ }
    }

    public void Touch(string path, int page)
    {
        var existing = Recent.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null) { existing = new RecentFile { Path = path }; Recent.Insert(0, existing); }
        else { Recent.Remove(existing); Recent.Insert(0, existing); }
        existing.Page = page;
        existing.Opened = DateTime.Now;
        if (Recent.Count > 30) Recent.RemoveRange(30, Recent.Count - 30);
    }

    public int LastPage(string path) =>
        Recent.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))?.Page ?? 0;
}
