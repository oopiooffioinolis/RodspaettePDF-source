using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Rodspaette.App;

/// <summary>Line icon drawn on a 24×24 grid (same geometry as the design mockup). Colour follows the parent control's Foreground.</summary>
public sealed class IconView : Viewbox
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(string), typeof(IconView), new PropertyMetadata(null, (d, _) => ((IconView)d).Rebuild()));
    public static readonly DependencyProperty FillDataProperty =
        DependencyProperty.Register(nameof(FillData), typeof(string), typeof(IconView), new PropertyMetadata(null, (d, _) => ((IconView)d).Rebuild()));
    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(IconView), new PropertyMetadata(null, (d, _) => ((IconView)d).Rebuild()));
    public static readonly DependencyProperty StrokeWidthProperty =
        DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(IconView), new PropertyMetadata(2.1, (d, _) => ((IconView)d).Rebuild()));

    public string? Data { get => (string?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public string? FillData { get => (string?)GetValue(FillDataProperty); set => SetValue(FillDataProperty, value); }
    public Brush? IconBrush { get => (Brush?)GetValue(IconBrushProperty); set => SetValue(IconBrushProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public IconView()
    {
        Width = 18;
        Height = 18;
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
    }

    public static IconView Make(string data, double size = 18, Brush? brush = null)
        => new() { Data = data, Width = size, Height = size, IconBrush = brush };

    private void Rebuild()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (!string.IsNullOrEmpty(Data))
        {
            var p = new Path
            {
                Data = Geometry.Parse(Data),
                StrokeThickness = StrokeWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = null,
            };
            Bind(p, Shape.StrokeProperty);
            canvas.Children.Add(p);
        }
        if (!string.IsNullOrEmpty(FillData))
        {
            var f = new Path { Data = Geometry.Parse(FillData) };
            Bind(f, Shape.FillProperty);
            canvas.Children.Add(f);
        }
        Child = canvas;
    }

    private void Bind(Path p, DependencyProperty prop)
    {
        if (IconBrush != null) { p.SetValue(prop, IconBrush); return; }
        p.SetBinding(prop, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
            FallbackValue = Theme.GetBrush("Text2"),
        });
    }
}

public static class Icons
{
    public const string ToolSelect = "M6 4l12 7-5.5 1.5L10 18z";
    public const string ToolHighlight = "M15 4l5 5-8.5 8.5H7v-4.5z M7 13l4 4";
    public const string ToolUnderline = "M7 4v6a5 5 0 0 0 10 0V4 M5 20h14";
    public const string ToolStrike = "M16 6.5C15.3 5 13.9 4 12 4c-2.5 0-4 1.4-4 3.2 0 1.3.8 2.2 2.4 2.8 M4 12h16 M8 17.2C8.7 19 10.2 20 12.3 20c2.6 0 4.2-1.4 4.2-3.3 0-.9-.3-1.6-1-2.2";
    public const string ToolPen = "M3 17c3-4 5-1 7-4s2-7 5-7 3 4 1 7-5 6-2 8 5-1 6-2";
    public const string ToolNote = "M5 4h14v11l-5 5H5z M14 20v-5h5";
    public const string ToolText = "M6.5 4h11A2.5 2.5 0 0 1 20 6.5v11a2.5 2.5 0 0 1-2.5 2.5h-11A2.5 2.5 0 0 1 4 17.5v-11A2.5 2.5 0 0 1 6.5 4z M9 9h6 M12 9v7";
    public const string Copy = "M9 9h9a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2z M5 15H4.5A1.5 1.5 0 0 1 3 13.5v-9A1.5 1.5 0 0 1 4.5 3h9A1.5 1.5 0 0 1 15 4.5V5";
    public const string Save = "M6 4h10l4 4v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M8 4v5h7V4 M8 20v-6h8v6";
    public const string SaveAs = "M6 4h10l4 4v3 M8 4v5h7V4 M11 20H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2 M14 20l1-3.5 5-5 2.5 2.5-5 5z";
    public const string Gear = "M12 9a3 3 0 1 1 0 6a3 3 0 1 1 0-6z M12 2.8l1.6 2.4 2.8-.7.6 2.8 2.6 1.3-1.2 2.6 1.2 2.6-2.6 1.3-.6 2.8-2.8-.7L12 21.2l-1.6-2.4-2.8.7-.6-2.8-2.6-1.3 1.2-2.6-1.2-2.6 2.6-1.3.6-2.8 2.8.7z";
    public const string Mouse = "M12 3a6 6 0 0 1 6 6v6a6 6 0 0 1-12 0V9a6 6 0 0 1 6-6z M12 3v6 M6 9h12";
    public const string Refresh = "M20 11a8 8 0 0 0-14.6-4.5 M4 5v4h4 M4 13a8 8 0 0 0 14.6 4.5 M20 19v-4h-4";
    public const string Report = "M12 4l9 16H3z M12 10v4.5 M12 17.5v.01";
    public const string Edit = "M4 20h4L19 9l-4-4L4 16v4z";
    public const string Sidebar = "M6 4h12a3 3 0 0 1 3 3v10a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3z M9 4v16";
    public const string Search = "M17.5 11a6.5 6.5 0 1 1-13 0a6.5 6.5 0 1 1 13 0z M16 16l4.5 4.5";
    public const string LookUp = "M15.5 13a5.5 5.5 0 1 1-11 0a5.5 5.5 0 1 1 11 0z M14 17l4.5 4.5 M18.5 2.5l.9 2.1 2.1.9-2.1.9-.9 2.1-.9-2.1-2.1-.9 2.1-.9z";
    public const string Bookmark = "M7 4h10v16l-5-4-5 4z";
    public const string Comment = "M5 5h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-7l-4.5 3.5V17H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2z";
    public const string Menu = "M4 6.5h16 M4 12h16 M4 17.5h8";
    public const string Circle = "M20 12a8 8 0 1 1-16 0a8 8 0 1 1 16 0z";
    public const string HalfFill = "M12 4a8 8 0 0 1 0 16z";
    public const string Print = "M7 9V4h10v5 M5.5 9h13a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z M8 14h8a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1z";
    public const string Markup = "M4 20h4L19 9l-4-4L4 16v4z M13.5 6.5l4 4";
    public const string Tabs = "M5 7h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2z M8 7V5a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3";
    public const string Minimize = "M5 12h14";
    public const string Maximize = "M7 5h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2z";
    public const string Restore = "M6.5 9h8a1.5 1.5 0 0 1 1.5 1.5v8a1.5 1.5 0 0 1-1.5 1.5h-8A1.5 1.5 0 0 1 5 18.5v-8A1.5 1.5 0 0 1 6.5 9z M9 6h8.5A1.5 1.5 0 0 1 19 7.5V16";
    public const string Close = "M6 6l12 12 M18 6L6 18";
    public const string ChevronDown = "M7 10l5 5 5-5";
    public const string ChevronRight = "M10 7l5 5-5 5";
    public const string ChevronUp = "M7 14l5-5 5 5";
    public const string Home = "M4 11l8-7 8 7 M6 9.5V20h12V9.5 M10 20v-5h4v5";
    public const string Focus = "M4 9V5a1 1 0 0 1 1-1h4 M15 4h4a1 1 0 0 1 1 1v4 M20 15v4a1 1 0 0 1-1 1h-4 M9 20H5a1 1 0 0 1-1-1v-4";
    public const string Cursor = "M6 4l12 7-5.5 1.5L10 18z";
    public const string Highlighter = "M15 4l5 5-8.5 8.5H7v-4.5z M7 13l4 4";
    public const string Underline = "M7 4v6a5 5 0 0 0 10 0V4 M5 20h14";
    public const string Strike = "M16 6.5C15.3 5 13.9 4 12 4c-2.5 0-4 1.4-4 3.2 0 1.3.8 2.2 2.4 2.8 M4 12h16 M8 17.2C8.7 19 10.2 20 12.3 20c2.6 0 4.2-1.4 4.2-3.3 0-.9-.3-1.6-1-2.2";
    public const string Pen = "M3 17c3-4 5-1 7-4s2-7 5-7 3 4 1 7-5 6-2 8 5-1 6-2";
    public const string Note = "M5 4h14v11l-5 5H5z M14 20v-5h5";
    public const string TextBox = "M6.5 4h11A2.5 2.5 0 0 1 20 6.5v11a2.5 2.5 0 0 1-2.5 2.5h-11A2.5 2.5 0 0 1 4 17.5v-11A2.5 2.5 0 0 1 6.5 4z M9 9h6 M12 9v7";
    public const string Undo = "M9 5L4 10l5 5 M4 10h10.5a5 5 0 0 1 0 10H12";
    public const string Minus = "M5 12h14";
    public const string Plus = "M12 5v14 M5 12h14";
    public const string Pin = "M9 4h6l-1 6 3 3H7l3-3z M12 13v7";
    public const string File = "M7 3h7l5 5v13H7z M14 3v5h5";
    public const string Trash = "M5 7h14 M10 7V5h4v2 M7 7l1 13h8l1-13";
    public const string Folder = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
    public const string Ocr = "M4 8V5a1 1 0 0 1 1-1h3 M16 4h3a1 1 0 0 1 1 1v3 M20 16v3a1 1 0 0 1-1 1h-3 M8 20H5a1 1 0 0 1-1-1v-3 M8 12h8";
    public const string FitWidth = "M4 6v12 M20 6v12 M8 12h8 M10 9l-3 3 3 3 M14 9l3 3-3 3";
}
