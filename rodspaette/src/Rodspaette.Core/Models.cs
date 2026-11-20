namespace Rodspaette.Core;

/// <summary>A rectangle in "page view" units: points (1/72 inch) at 100 % zoom, origin top-left, y down.</summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public bool IntersectsWith(ViewRect o) => o.X < Right && o.Right > X && o.Y < Bottom && o.Bottom > Y;
    public static ViewRect FromLTRB(double l, double t, double r, double b) =>
        new(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t));
    public ViewRect Union(ViewRect o) => FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    public ViewRect Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

public readonly record struct ViewPoint(double X, double Y);

public readonly record struct PageSize(double Width, double Height);

/// <summary>Maps page view units (points, y down, origin top-left of the displayed page) to PDF user space and back.</summary>
public sealed class PageTransform
{
    // view -> pdf:  px = A*vx + C*vy + E ;  py = B*vx + D*vy + F
    public double A, B, C, D, E, F;
    private double _ia, _ib, _ic, _id, _ie, _if;

    public PageTransform(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
        double det = a * d - b * c;
        if (Math.Abs(det) < 1e-12) det = 1e-12;
        _ia = d / det; _ib = -b / det; _ic = -c / det; _id = a / det;
        _ie = -(e * _ia + f * _ic);
        _if = -(e * _ib + f * _id);
    }

    public (double x, double y) ViewToPdf(double vx, double vy) => (A * vx + C * vy + E, B * vx + D * vy + F);
    public (double x, double y) PdfToView(double px, double py) => (_ia * px + _ic * py + _ie, _ib * px + _id * py + _if);

    public ViewRect PdfRectToView(double l, double t, double r, double b)
    {
        var (x1, y1) = PdfToView(l, t);
        var (x2, y2) = PdfToView(r, b);
        return ViewRect.FromLTRB(x1, y1, x2, y2);
    }
}

public sealed class TextChar
{
    public char Char;
    public ViewRect Box;      // loose box in view units (consistent line height, good for selection)
    public bool HasBox;
}

/// <summary>All characters of a page with their boxes, used by the UI for hit-testing and selection without round-trips.</summary>
public sealed class PageText
{
    public required int PageIndex { get; init; }
    public required TextChar[] Chars { get; init; }
    public required string Text { get; init; }
    public bool IsEmpty => Chars.Length == 0 || string.IsNullOrWhiteSpace(Text);
    /// <summary>True when the characters came from OCR rather than the PDF's own text layer.</summary>
    public bool FromOcr { get; init; }
}

public sealed class OutlineItem
{
    public string Title { get; set; } = "";
    public int PageIndex { get; set; } = -1;
    /// <summary>Target position on the page in view units (y down), or null for "top of page".</summary>
    public double? ViewTop { get; set; }
    /// <summary>Raw PDF y of the destination (kept for writing the outline back).</summary>
    public double? PdfTop { get; set; }
    public string? Uri { get; set; }
    public bool IsOpen { get; set; }
    public List<OutlineItem> Children { get; } = new();
    public int Depth { get; set; }
}

public sealed class SearchHit
{
    public required int PageIndex { get; init; }
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required List<ViewRect> Rects { get; init; }
    public required string Before { get; init; }
    public required string Match { get; init; }
    public required string After { get; init; }
    public bool FromOcr { get; init; }
}

public enum AnnotKind { Highlight, Underline, StrikeOut, Squiggly, Ink, Note, FreeText, Other }

public sealed class AnnotInfo
{
    public required int PageIndex { get; init; }
    public required int Index { get; init; }
    public required AnnotKind Kind { get; init; }
    public required ViewRect Bounds { get; init; }
    public uint ColorArgb { get; init; }
    public string Contents { get; init; } = "";
    /// <summary>For text markup: the text that is marked.</summary>
    public string MarkedText { get; init; } = "";
    /// <summary>For text boxes: the text's size in points and its colour (0 = not given), from the box's /DA.</summary>
    public double FontSize { get; init; }
    public uint TextArgb { get; init; }
}

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new RgbColor(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }
}

public sealed class PdfPasswordRequiredException : Exception
{
    public bool WrongPassword { get; }
    public PdfPasswordRequiredException(bool wrong) : base(wrong ? "Wrong password" : "Password required") { WrongPassword = wrong; }
}

public sealed class PdfOpenException : Exception
{
    public PdfOpenException(string message) : base(message) { }
}
