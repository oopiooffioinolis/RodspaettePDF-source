using System.Globalization;
using System.Text;

namespace Rodspaette.Core;

public static class PageTextExtensions
{
    /// <summary>Rectangles (one per line) covering chars [start, start+count).</summary>
    public static List<ViewRect> GetRangeRects(this PageText text, int start, int count)
    {
        var result = new List<ViewRect>();
        int end = Math.Min(text.Chars.Length, start + count);
        ViewRect? line = null;
        for (int i = Math.Max(0, start); i < end; i++)
        {
            var c = text.Chars[i];
            if (!c.HasBox) continue;
            if (line is { } l)
            {
                double lcy = l.Y + l.Height / 2, ccy = c.Box.Y + c.Box.Height / 2;
                bool sameLine = Math.Abs(lcy - ccy) < Math.Max(l.Height, c.Box.Height) * 0.5 && c.Box.X >= l.X - 2;
                if (sameLine) { line = l.Union(c.Box); continue; }
                result.Add(l);
            }
            line = c.Box;
        }
        if (line is { } last) result.Add(last);
        return result;
    }

    /// <summary>Index of the character nearest to a point (view units), preferring the same line. -1 if the page has no text.</summary>
    public static int HitTest(this PageText text, double x, double y)
    {
        int best = -1;
        double bestScore = double.MaxValue;
        var chars = text.Chars;
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!c.HasBox) continue;
            var b = c.Box;
            double dy = y < b.Y ? b.Y - y : y > b.Bottom ? y - b.Bottom : 0;
            double dx = x < b.X ? b.X - x : x > b.Right ? x - b.Right : 0;
            double score = dy * 6 + dx;
            if (score < bestScore) { bestScore = score; best = i; if (score == 0) break; }
        }
        return best;
    }

    /// <summary>Exact hit: the index of the char whose box contains the point, else -1.</summary>
    public static int CharAt(this PageText text, double x, double y)
    {
        var chars = text.Chars;
        for (int i = 0; i < chars.Length; i++)
            if (chars[i].HasBox && chars[i].Box.Inflate(0.5).Contains(x, y)) return i;
        return -1;
    }

    /// <summary>Expands an index to the surrounding word.</summary>
    public static (int start, int count) WordAt(this PageText text, int index)
    {
        var s = text.Text;
        if (index < 0 || index >= s.Length) return (index, 0);
        static bool W(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '-' || c == '_';
        if (!W(s[index])) return (index, 1);
        int a = index, b = index;
        while (a > 0 && W(s[a - 1])) a--;
        while (b < s.Length - 1 && W(s[b + 1])) b++;
        return (a, b - a + 1);
    }

    public static string GetRangeText(this PageText text, int start, int count)
    {
        int end = Math.Min(text.Text.Length, start + count);
        if (start < 0 || start >= end) return "";
        var raw = text.Text.Substring(start, end - start);
        // normalise PDF line breaks: keep paragraph breaks, join hyphenless line wraps with a space
        raw = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(raw.Length);
        foreach (var line in raw.Split('\n'))
        {
            var t = line.TrimEnd();
            if (sb.Length > 0)
            {
                if (sb[^1] == '-') sb.Length--; // de-hyphenate
                else sb.Append(' ');
            }
            sb.Append(t);
        }
        return sb.ToString().Trim();
    }
}

public static class TextSearch
{
    public static SearchHit MakeHit(PageText text, int start, int count, List<ViewRect> rects, bool fromOcr)
    {
        string s = text.Text;
        int a = Math.Max(0, start - 40);
        int e = Math.Min(s.Length, start + count + 60);
        string before = Clean(s.Substring(a, start - a));
        string match = Clean(s.Substring(start, Math.Min(count, s.Length - start)));
        string after = Clean(s.Substring(start + count, Math.Max(0, e - start - count)));
        if (a > 0) before = "…" + before.TrimStart();
        if (e < s.Length) after = after.TrimEnd() + "…";
        return new SearchHit
        {
            PageIndex = text.PageIndex, Start = start, Length = count, Rects = rects,
            Before = before, Match = match, After = after, FromOcr = fromOcr,
        };
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || ch == '￾' || ch == '\u0002') { if (!space) sb.Append(' '); space = true; }
            else { sb.Append(ch); space = false; }
        }
        return sb.ToString();
    }

    /// <summary>Managed search used for OCR text (and as a fallback).</summary>
    public static List<SearchHit> Find(PageText text, string query, bool matchCase, bool wholeWord, bool fromOcr)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(query) || text.Text.Length == 0) return hits;
        var cmp = CultureInfo.CurrentCulture.CompareInfo;
        var opts = matchCase ? CompareOptions.None : CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        // collapse whitespace in the haystack mapping back to original indices
        string s = text.Text;
        int from = 0;
        while (from < s.Length)
        {
            int idx = cmp.IndexOf(s, query, from, opts);
            if (idx < 0) break;
            int len = query.Length;
            bool ok = true;
            if (wholeWord)
            {
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(s[idx - 1]);
                bool rightOk = idx + len >= s.Length || !char.IsLetterOrDigit(s[idx + len]);
                ok = leftOk && rightOk;
            }
            if (ok) hits.Add(MakeHit(text, idx, len, text.GetRangeRects(idx, len), fromOcr));
            from = idx + Math.Max(1, len);
        }
        return hits;
    }
}

/// <summary>An OCR word: text plus its box in view units.</summary>
public readonly record struct OcrWord(string Text, ViewRect Box, int LineId);

public static class OcrText
{
    /// <summary>Builds a selectable/searchable text layer from OCR words: each character gets a slice of its word's box.</summary>
    public static PageText FromWords(int pageIndex, IReadOnlyList<OcrWord> words)
    {
        var chars = new List<TextChar>();
        var sb = new StringBuilder();
        int lastLine = int.MinValue;
        for (int w = 0; w < words.Count; w++)
        {
            var word = words[w];
            if (string.IsNullOrEmpty(word.Text)) continue;
            if (chars.Count > 0)
            {
                bool newLine = word.LineId != lastLine;
                char sep = newLine ? '\n' : ' ';
                var prev = chars[^1];
                // separator gets a box spanning the gap on the same line (so selection looks continuous)
                var sepChar = new TextChar { Char = sep };
                if (!newLine && prev.HasBox)
                {
                    double gx = prev.Box.Right, gw = Math.Max(0.1, word.Box.X - prev.Box.Right);
                    sepChar.Box = new ViewRect(gx, Math.Min(prev.Box.Y, word.Box.Y), gw, Math.Max(prev.Box.Height, word.Box.Height));
                    sepChar.HasBox = true;
                }
                chars.Add(sepChar);
                sb.Append(sep);
            }
            lastLine = word.LineId;
            int n = word.Text.Length;
            double cw = word.Box.Width / n;
            for (int i = 0; i < n; i++)
            {
                chars.Add(new TextChar
                {
                    Char = word.Text[i],
                    Box = new ViewRect(word.Box.X + i * cw, word.Box.Y, cw, word.Box.Height),
                    HasBox = true,
                });
                sb.Append(word.Text[i]);
            }
        }
        return new PageText { PageIndex = pageIndex, Chars = chars.ToArray(), Text = sb.ToString(), FromOcr = true };
    }
}

/// <summary>Vector path in view units, used to give text boxes a font-independent appearance.</summary>
public readonly record struct PathCommand(char Op, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0, double X3 = 0, double Y3 = 0)
{
    public static PathCommand Move(double x, double y) => new('m', x, y);
    public static PathCommand Line(double x, double y) => new('l', x, y);
    public static PathCommand Cubic(double x1, double y1, double x2, double y2, double x3, double y3) => new('c', x1, y1, x2, y2, x3, y3);
    public static PathCommand Close() => new('h');

    public static string ToPdfContent(IReadOnlyList<PathCommand> path, PageTransform t, RgbColor color)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendFormat(inv, "q {0:0.###} {1:0.###} {2:0.###} rg\n", color.R / 255.0, color.G / 255.0, color.B / 255.0);
        bool any = false;
        foreach (var c in path)
        {
            switch (c.Op)
            {
                case 'm':
                {
                    var (x, y) = t.ViewToPdf(c.X1, c.Y1);
                    sb.AppendFormat(inv, "{0:0.###} {1:0.###} m\n", x, y); any = true; break;
                }
                case 'l':
                {
                    var (x, y) = t.ViewToPdf(c.X1, c.Y1);
                    sb.AppendFormat(inv, "{0:0.###} {1:0.###} l\n", x, y); break;
                }
                case 'c':
                {
                    var (x1, y1) = t.ViewToPdf(c.X1, c.Y1);
                    var (x2, y2) = t.ViewToPdf(c.X2, c.Y2);
                    var (x3, y3) = t.ViewToPdf(c.X3, c.Y3);
                    sb.AppendFormat(inv, "{0:0.###} {1:0.###} {2:0.###} {3:0.###} {4:0.###} {5:0.###} c\n", x1, y1, x2, y2, x3, y3); break;
                }
                case 'h': sb.Append("h\n"); break;
            }
        }
        if (any) sb.Append("f\n");
        sb.Append("Q\n");
        return sb.ToString();
    }
}

/// <summary>Colour post-processing for the dark and green reading themes (applied to rendered BGRA pixels).</summary>
public static class PageTint
{
    /// <summary>Dark: inverts lightness but keeps hue, mapping white paper to a dark grey and black ink to light grey.</summary>
    public static void ApplyDark(Span<byte> bgra, byte paper = 38, byte ink = 230)
    {
        double range = ink - paper;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            double y = 0.299 * r + 0.587 * g + 0.114 * b;
            double ny = paper + range * (1 - y / 255.0);
            double k = 0.85;
            bgra[i] = Clamp(ny + (b - y) * k);
            bgra[i + 1] = Clamp(ny + (g - y) * k);
            bgra[i + 2] = Clamp(ny + (r - y) * k);
        }
    }

    /// <summary>Green (or any paper colour): multiplies, so white becomes the paper colour and black stays black.</summary>
    public static void ApplyMultiply(Span<byte> bgra, RgbColor paper)
    {
        int pr = paper.R, pg = paper.G, pb = paper.B;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(bgra[i] * pb / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * pg / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * pr / 255);
        }
    }

    private static byte Clamp(double v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5);
}
