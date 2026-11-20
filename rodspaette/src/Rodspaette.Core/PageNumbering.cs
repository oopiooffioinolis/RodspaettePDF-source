using System.Globalization;
using System.Text;

namespace Rodspaette.Core;

public enum PageLabelStyle { None, Decimal, RomanUpper, RomanLower, LettersUpper, LettersLower }

/// <summary>One range of a PDF's page numbering (/PageLabels): from page <see cref="Start"/> (0-based) until the
/// next range, the pages read <see cref="Prefix"/> + <see cref="First"/>, First + 1, … in <see cref="Style"/>.</summary>
public sealed record PageLabelRange(int Start, PageLabelStyle Style, string Prefix, int First);

/// <summary>One page's number. A number below 1 can't be written into a PDF; it marks a page that needs another numbering.</summary>
public readonly record struct PageNumber(PageLabelStyle Style, string Prefix, int Number)
{
    public string Label => PageNumbering.Format(Style, Prefix, Number);
    public bool IsNumbered => Style != PageLabelStyle.None;
}

public enum NumberingProblemKind { BelowOne, NoNumber, SameAsOther }

/// <summary>Pages <see cref="From"/>–<see cref="To"/> (0-based) need a numbering of their own; for
/// <see cref="NumberingProblemKind.SameAsOther"/>, <see cref="OtherFrom"/>–<see cref="OtherTo"/> already use those numbers.</summary>
public sealed record NumberingProblem(NumberingProblemKind Kind, int From, int To, int OtherFrom = -1, int OtherTo = -1);

/// <summary>A numbering for a run of pages: <see cref="Prefix"/> + <see cref="First"/>, First + 1, … in <see cref="Style"/>.</summary>
public sealed record NumberingChoice(string Title, PageLabelStyle Style, string Prefix, int First)
{
    public PageNumber At(int k) => new(Style, Prefix, First + k);
}

/// <summary>
/// A PDF's page numbering ("page labels"): what each page is called, as opposed to where it is in the file.
/// Reading and writing /PageLabels, and the checks that keep every page with exactly one number of its own.
/// </summary>
public static class PageNumbering
{
    // ================================================================= labels
    public static string Format(PageLabelStyle style, string prefix, int n) => prefix + Number(style, n);

    public static string Number(PageLabelStyle style, int n)
    {
        if (n < 1) return "";
        return style switch
        {
            PageLabelStyle.Decimal => n.ToString(CultureInfo.InvariantCulture),
            PageLabelStyle.RomanUpper => Roman(n),
            PageLabelStyle.RomanLower => Roman(n).ToLowerInvariant(),
            PageLabelStyle.LettersUpper => Letters(n),
            PageLabelStyle.LettersLower => Letters(n).ToLowerInvariant(),
            _ => "",
        };
    }

    private static string Roman(int n)
    {
        string[] h = { "", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM" };
        string[] t = { "", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC" };
        string[] o = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" };
        return new string('M', n / 1000) + h[n / 100 % 10] + t[n / 10 % 10] + o[n % 10];
    }

    /// <summary>A–Z, then AA–ZZ, AAA–ZZZ … (the PDF standard's letter numbering).</summary>
    private static string Letters(int n) => new((char)('A' + (n - 1) % 26), (n - 1) / 26 + 1);

    /// <summary>"page 5" or "pages 1–13" (1-based, for messages).</summary>
    public static string Pages(int from, int to) => from == to ? $"page {from + 1}" : $"pages {from + 1}–{to + 1}";

    // ================================================================= ranges and pages
    /// <summary>Every page's number. No ranges means the plain 1, 2, 3 … that readers show without page labels.</summary>
    public static PageNumber[] Expand(IReadOnlyList<PageLabelRange>? ranges, int pageCount)
    {
        var a = new PageNumber[pageCount];
        var sorted = (ranges ?? Array.Empty<PageLabelRange>()).OrderBy(r => r.Start).ToList();
        int ri = -1;
        for (int i = 0; i < pageCount; i++)
        {
            while (ri + 1 < sorted.Count && sorted[ri + 1].Start <= i) ri++;
            if (ri < 0) a[i] = new PageNumber(PageLabelStyle.Decimal, "", i + 1);
            else
            {
                var r = sorted[ri];
                a[i] = new PageNumber(r.Style, r.Prefix, r.First + (i - r.Start));
            }
        }
        return a;
    }

    public static PageNumber[] Plain(int pageCount) => Expand(null, pageCount);

    /// <summary>Does page <paramref name="b"/> carry on from page <paramref name="a"/> (same style and prefix, next number)?</summary>
    public static bool Continues(PageNumber a, PageNumber b) =>
        a.Style == b.Style && a.Prefix == b.Prefix && (a.Style == PageLabelStyle.None || b.Number == a.Number + 1);

    /// <summary>The fewest ranges that give every page its number.</summary>
    public static List<PageLabelRange> Compress(IReadOnlyList<PageNumber> pages)
    {
        var list = new List<PageLabelRange>();
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            if (i > 0 && Continues(pages[i - 1], p)) continue;
            list.Add(new PageLabelRange(i, p.Style, p.Prefix, p.IsNumbered ? Math.Max(1, p.Number) : 1));
        }
        return list;
    }

    /// <summary>Plain 1, 2, 3 …: the same as having no page labels at all.</summary>
    public static bool IsPlain(IReadOnlyList<PageNumber> pages)
    {
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Style != PageLabelStyle.Decimal || pages[i].Prefix.Length != 0 || pages[i].Number != i + 1) return false;
        return true;
    }

    /// <summary>The pages that share a range with page <paramref name="index"/> (continue each other's numbering).</summary>
    public static (int From, int To) SectionOf(IReadOnlyList<PageNumber> pages, int index)
    {
        int s = index, e = index;
        while (s > 0 && Continues(pages[s - 1], pages[s])) s--;
        while (e + 1 < pages.Count && Continues(pages[e], pages[e + 1])) e++;
        return (s, e);
    }

    // ================================================================= the edit and its checks
    /// <summary>Pages <paramref name="from"/>–<paramref name="to"/> become one run in which page
    /// <paramref name="refPage"/> reads <paramref name="refNumber"/> (pages before it count down, so some may fall below 1).</summary>
    public static void Renumber(PageNumber[] pages, bool[] locked, int from, int to, int refPage, int refNumber, PageLabelStyle style, string prefix)
    {
        for (int i = from; i <= to; i++)
        {
            pages[i] = new PageNumber(style, prefix, refNumber + (i - refPage));
            locked[i] = true;
        }
    }

    /// <summary>
    /// The first run of pages that needs a numbering of its own, or null when every page has exactly one label that
    /// no other page has. In order: numbers below 1; pages with no number; numbers used twice, where the pages to
    /// renumber are the ones the user didn't just set (<paramref name="locked"/>), taken with the rest of their range.
    /// </summary>
    public static NumberingProblem? FirstProblem(IReadOnlyList<PageNumber> pages, IReadOnlyList<bool> locked)
    {
        int n = pages.Count;
        for (int i = 0; i < n; i++)
        {
            if (!pages[i].IsNumbered || pages[i].Number >= 1) continue;
            int j = i;
            while (j + 1 < n && pages[j + 1].IsNumbered && pages[j + 1].Number < 1) j++;
            return new NumberingProblem(NumberingProblemKind.BelowOne, i, j);
        }

        var labels = new string[n];
        for (int i = 0; i < n; i++) labels[i] = pages[i].Label;
        for (int i = 0; i < n; i++)
        {
            if (labels[i].Length > 0) continue;
            int j = i;
            while (j + 1 < n && labels[j + 1].Length == 0) j++;
            return new NumberingProblem(NumberingProblemKind.NoNumber, i, j);
        }

        var where = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (!where.TryGetValue(labels[i], out var l)) where[labels[i]] = l = new List<int>();
            l.Add(i);
        }
        int pick = int.MaxValue;
        foreach (var l in where.Values)
        {
            if (l.Count < 2) continue;
            var free = l.Where(i => !locked[i]).ToList();
            // renumber pages the user didn't set; if all (or none) were set, keep the first and renumber the next
            int cand = free.Count == 0 || free.Count == l.Count ? l[1] : free[0];
            pick = Math.Min(pick, cand);
        }
        if (pick == int.MaxValue) return null;

        bool lk = locked[pick];
        int s = pick, e = pick;
        while (s > 0 && locked[s - 1] == lk && Continues(pages[s - 1], pages[s])) s--;
        while (e + 1 < n && locked[e + 1] == lk && Continues(pages[e], pages[e + 1])) e++;
        var mine = new HashSet<string>(StringComparer.Ordinal);
        for (int i = s; i <= e; i++) mine.Add(labels[i]);
        int of = -1, ot = -1;
        for (int i = 0; i < n; i++)
            if ((i < s || i > e) && mine.Contains(labels[i])) { if (of < 0) of = i; ot = i; }
        return new NumberingProblem(NumberingProblemKind.SameAsOther, s, e, of, ot);
    }

    /// <summary>The usual ways to number a problem's pages: carry on from the page before (when it has a number), then
    /// i ii iii, I II III, a b c, A B C and 1 2 3.</summary>
    public static List<NumberingChoice> Choices(IReadOnlyList<PageNumber> pages, NumberingProblem p)
    {
        var list = new List<NumberingChoice>();
        if (p.From > 0 && pages[p.From - 1].IsNumbered && pages[p.From - 1].Number >= 1)
        {
            var prev = pages[p.From - 1];
            list.Add(new NumberingChoice($"Carry on from page {p.From}", prev.Style, prev.Prefix, prev.Number + 1));
        }
        list.Add(new NumberingChoice("i, ii, iii …", PageLabelStyle.RomanLower, "", 1));
        list.Add(new NumberingChoice("I, II, III …", PageLabelStyle.RomanUpper, "", 1));
        list.Add(new NumberingChoice("a, b, c …", PageLabelStyle.LettersLower, "", 1));
        list.Add(new NumberingChoice("A, B, C …", PageLabelStyle.LettersUpper, "", 1));
        list.Add(new NumberingChoice("1, 2, 3 …", PageLabelStyle.Decimal, "", 1));
        return list;
    }

    /// <summary>"i – xiii": the first and last label a choice gives pages <paramref name="from"/>–<paramref name="to"/>.</summary>
    public static string Preview(NumberingChoice c, int from, int to)
    {
        string a = c.At(0).Label, b = c.At(to - from).Label;
        return from == to ? a : $"{a} – {b}";
    }

    /// <summary>Why a choice can't be used for pages <paramref name="from"/>–<paramref name="to"/>, or null if it can: it
    /// would leave a page without a number, or give a page a number that a page already settled
    /// (<paramref name="locked"/>) has. Numbers shared with pages not yet settled are fine: those are asked about next.</summary>
    public static string? Clash(IReadOnlyList<PageNumber> pages, IReadOnlyList<bool> locked, int from, int to, NumberingChoice c)
    {
        if (c.First < 1) return "numbers start at 1";
        if (c.Style == PageLabelStyle.None && (c.Prefix.Length == 0 || to > from)) return "every page needs a number";
        var mine = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k <= to - from; k++) mine.Add(c.At(k).Label);
        int of = -1, ot = -1;
        for (int i = 0; i < pages.Count; i++)
            if ((i < from || i > to) && locked[i] && mine.Contains(pages[i].Label)) { if (of < 0) of = i; ot = i; }
        return of < 0 ? null : "same numbers as " + Pages(of, ot);
    }

    public static void Apply(PageNumber[] pages, bool[] locked, int from, int to, NumberingChoice c)
    {
        for (int k = 0; k <= to - from; k++)
        {
            pages[from + k] = c.At(k);
            locked[from + k] = true;
        }
    }

    // ================================================================= /PageLabels in the file
    /// <summary>The PDF's page numbering as ranges, or null when it has none. Throws <see cref="NotSupportedException"/>
    /// for encrypted PDFs (their label prefixes can't be read here).</summary>
    public static List<PageLabelRange>? Read(byte[] pdf)
    {
        var parser = new PdfMiniParser(pdf);
        if (parser.Trailer["Encrypt"] != null) throw new NotSupportedException("This PDF is encrypted.");
        var catalog = parser.Resolve(parser.Trailer["Root"]) as PDict;
        if (parser.Resolve(catalog?["PageLabels"]) is not PDict tree) return null;

        var pairs = new List<(int Key, PDict Dict)>();
        var seen = new HashSet<int>();
        void Collect(PDict node, int depth)
        {
            if (depth > 32) return;
            if (parser.Resolve(node["Nums"]) is PArray nums)
                for (int i = 0; i + 1 < nums.Items.Count; i += 2)
                    if (parser.Resolve(nums.Items[i]) is PNum k && parser.Resolve(nums.Items[i + 1]) is PDict d)
                        pairs.Add((k.Int, d));
            if (parser.Resolve(node["Kids"]) is PArray kids)
                foreach (var kid in kids.Items)
                {
                    if (kid is PRef r && !seen.Add(r.Num)) continue;
                    if (parser.Resolve(kid) is PDict kd) Collect(kd, depth + 1);
                }
        }
        Collect(tree, 0);
        if (pairs.Count == 0) return null;

        var ranges = new List<PageLabelRange>();
        foreach (var (key, d) in pairs.OrderBy(p => p.Key))
        {
            if (key < 0 || ranges.Count > 0 && ranges[^1].Start == key) continue;
            var style = (parser.Resolve(d["S"]) as PName)?.V switch
            {
                "D" => PageLabelStyle.Decimal,
                "R" => PageLabelStyle.RomanUpper,
                "r" => PageLabelStyle.RomanLower,
                "A" => PageLabelStyle.LettersUpper,
                "a" => PageLabelStyle.LettersLower,
                _ => PageLabelStyle.None,
            };
            string prefix = parser.Resolve(d["P"]) is PStr ps ? DecodeText(ps.Bytes) : "";
            int first = parser.Resolve(d["St"]) is PNum st ? Math.Max(1, st.Int) : 1;
            ranges.Add(new PageLabelRange(key, style, prefix, first));
        }
        return ranges;
    }

    /// <summary>
    /// Writes the page numbering into the PDF as an incremental update (the rest of the file is kept byte for byte).
    /// Plain 1, 2, 3 … (or null) removes /PageLabels. Every range gets /St, since some readers ignore a range without it.
    /// </summary>
    public static byte[] Write(byte[] pdf, IReadOnlyList<PageLabelRange>? ranges, int pageCount)
    {
        var parser = new PdfMiniParser(pdf);
        if (parser.Trailer["Encrypt"] != null)
            throw new NotSupportedException("Page numbers cannot be saved into encrypted PDFs.");
        var rootRef = (PRef)parser.Trailer["Root"]!;
        var catalog = parser.Resolve(rootRef) as PDict ?? throw new PdfParseException("Catalog is not a dictionary");

        var pages = Expand(ranges, pageCount);
        int next = Math.Max(parser.Size, 1);
        var objects = new List<(int num, int gen, string body)>();
        if (IsPlain(pages))
        {
            catalog["PageLabels"] = null;
        }
        else
        {
            var nums = new PArray();
            foreach (var r in Compress(pages))
            {
                var d = new PDict();
                d["Type"] = new PName("PageLabel");
                string? s = r.Style switch
                {
                    PageLabelStyle.Decimal => "D",
                    PageLabelStyle.RomanUpper => "R",
                    PageLabelStyle.RomanLower => "r",
                    PageLabelStyle.LettersUpper => "A",
                    PageLabelStyle.LettersLower => "a",
                    _ => null,
                };
                if (s != null) d["S"] = new PName(s);
                if (r.Prefix.Length > 0) d["P"] = PdfSerializer.TextString(r.Prefix);
                d["St"] = new PNum(r.First);
                nums.Items.Add(new PNum(r.Start));
                nums.Items.Add(d);
            }
            var tree = new PDict();
            tree["Nums"] = nums;
            int num = next++;
            var tsb = new StringBuilder();
            PdfSerializer.Write(tsb, tree);
            objects.Add((num, 0, tsb.ToString()));
            catalog["PageLabels"] = new PRef(num, 0);
        }
        var csb = new StringBuilder();
        PdfSerializer.Write(csb, catalog);
        objects.Add((rootRef.Num, rootRef.Gen, csb.ToString()));
        return IncrementalUpdate.Append(pdf, parser, rootRef, objects, next);
    }

    /// <summary>A PDF text string: UTF-16 or UTF-8 with a byte-order mark, otherwise PDFDocEncoding (close to Latin-1).</summary>
    public static string DecodeText(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return Encoding.Latin1.GetString(b);
    }
}
