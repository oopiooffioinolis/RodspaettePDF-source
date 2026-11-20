using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Rodspaette.Core;

// =====================================================================================
//  Minimal PDF object model + parser, just enough to rewrite the document outline
//  (bookmarks) as an incremental update. PDFium has no API for writing bookmarks.
// =====================================================================================

public abstract class PObj { }
public sealed class PNull : PObj { public static readonly PNull I = new(); }
public sealed class PBool : PObj { public bool V; public PBool(bool v) { V = v; } }
public sealed class PNum : PObj
{
    public string Raw;
    public PNum(string raw) { Raw = raw; }
    public PNum(double v) { Raw = v.ToString("0.####", CultureInfo.InvariantCulture); }
    public double Value => double.Parse(Raw, CultureInfo.InvariantCulture);
    public int Int => (int)Value;
}
public sealed class PStr : PObj { public byte[] Bytes; public PStr(byte[] b) { Bytes = b; } }
public sealed class PName : PObj { public string V; public PName(string v) { V = v; } }
public sealed class PArray : PObj { public List<PObj> Items = new(); }
public sealed class PRef : PObj { public int Num, Gen; public PRef(int n, int g) { Num = n; Gen = g; } }
public sealed class PDict : PObj
{
    public List<KeyValuePair<string, PObj>> Entries = new();
    public PObj? this[string key]
    {
        get { foreach (var e in Entries) if (e.Key == key) return e.Value; return null; }
        set
        {
            int i = Entries.FindIndex(e => e.Key == key);
            if (value == null) { if (i >= 0) Entries.RemoveAt(i); return; }
            if (i >= 0) Entries[i] = new(key, value); else Entries.Add(new(key, value));
        }
    }
}
public sealed class PStream : PObj { public PDict Dict = new(); public byte[] Data = Array.Empty<byte>(); }

public sealed class PdfParseException : Exception { public PdfParseException(string m) : base(m) { } }

public sealed class PdfMiniParser
{
    private readonly byte[] _b;
    private readonly Dictionary<int, (int type, long a, int b)> _xref = new();
    private readonly Dictionary<int, List<(int num, PObj obj)>> _objStmCache = new();
    public PDict Trailer { get; private set; } = new();
    public long LastXrefOffset { get; private set; }
    public bool LastXrefIsStream { get; private set; }
    public int Size { get; private set; }

    public PdfMiniParser(byte[] bytes)
    {
        _b = bytes;
        LoadXref();
    }

    // ------------------------------------------------------------- lexer
    private static bool IsWs(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelim(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private int SkipWs(int p)
    {
        while (p < _b.Length)
        {
            if (IsWs(_b[p])) { p++; continue; }
            if (_b[p] == '%') { while (p < _b.Length && _b[p] != 10 && _b[p] != 13) p++; continue; }
            break;
        }
        return p;
    }

    private string ReadToken(ref int p)
    {
        p = SkipWs(p);
        int s = p;
        while (p < _b.Length && !IsWs(_b[p]) && !IsDelim(_b[p])) p++;
        return Encoding.ASCII.GetString(_b, s, p - s);
    }

    private static bool IsNumber(string t) => t.Length > 0 && (char.IsDigit(t[0]) || t[0] is '-' or '+' or '.');

    public PObj ParseObject(ref int p)
    {
        p = SkipWs(p);
        if (p >= _b.Length) throw new PdfParseException("Unexpected end of file");
        byte c = _b[p];
        if (c == '/')
        {
            p++;
            int s = p;
            while (p < _b.Length && !IsWs(_b[p]) && !IsDelim(_b[p])) p++;
            return new PName(DecodeName(Encoding.Latin1.GetString(_b, s, p - s)));
        }
        if (c == '<' && p + 1 < _b.Length && _b[p + 1] == '<')
        {
            p += 2;
            var d = new PDict();
            while (true)
            {
                p = SkipWs(p);
                if (p + 1 < _b.Length && _b[p] == '>' && _b[p + 1] == '>') { p += 2; break; }
                var key = ParseObject(ref p) as PName ?? throw new PdfParseException("Dictionary key expected");
                var val = ParseObject(ref p);
                d.Entries.Add(new(key.V, val));
            }
            return d;
        }
        if (c == '<')
        {
            p++;
            var hex = new StringBuilder();
            while (p < _b.Length && _b[p] != '>') { if (!IsWs(_b[p])) hex.Append((char)_b[p]); p++; }
            p++;
            if (hex.Length % 2 == 1) hex.Append('0');
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.ToString(i * 2, 2), 16);
            return new PStr(bytes);
        }
        if (c == '(') return new PStr(ParseLiteral(ref p));
        if (c == '[')
        {
            p++;
            var a = new PArray();
            while (true)
            {
                p = SkipWs(p);
                if (p < _b.Length && _b[p] == ']') { p++; break; }
                a.Items.Add(ParseObject(ref p));
            }
            return a;
        }
        string tok = ReadToken(ref p);
        if (tok == "true") return new PBool(true);
        if (tok == "false") return new PBool(false);
        if (tok == "null") return PNull.I;
        if (IsNumber(tok))
        {
            // could be "n g R"
            int save = p;
            if (!tok.Contains('.') && !tok.StartsWith('-') && !tok.StartsWith('+'))
            {
                string t2 = ReadToken(ref p);
                if (t2.Length > 0 && t2.All(char.IsDigit))
                {
                    string t3 = ReadToken(ref p);
                    if (t3 == "R") return new PRef(int.Parse(tok), int.Parse(t2));
                }
            }
            p = save;
            return new PNum(tok);
        }
        throw new PdfParseException($"Unexpected token '{tok}'");
    }

    private byte[] ParseLiteral(ref int p)
    {
        var o = new List<byte>();
        p++; // (
        int depth = 1;
        while (p < _b.Length)
        {
            byte c = _b[p++];
            if (c == '\\')
            {
                if (p >= _b.Length) break;
                byte e = _b[p++];
                switch (e)
                {
                    case (byte)'n': o.Add(10); break;
                    case (byte)'r': o.Add(13); break;
                    case (byte)'t': o.Add(9); break;
                    case (byte)'b': o.Add(8); break;
                    case (byte)'f': o.Add(12); break;
                    case 13: if (p < _b.Length && _b[p] == 10) p++; break;
                    case 10: break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && p < _b.Length && _b[p] >= '0' && _b[p] <= '7'; k++) v = v * 8 + (_b[p++] - '0');
                            o.Add((byte)v);
                        }
                        else o.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(') depth++;
            if (c == ')' && --depth == 0) break;
            o.Add(c);
        }
        return o.ToArray();
    }

    private static string DecodeName(string raw)
    {
        if (!raw.Contains('#')) return raw;
        var sb = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length) { sb.Append((char)Convert.ToByte(raw.Substring(i + 1, 2), 16)); i += 2; }
            else sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------- indirect objects
    private PObj ParseIndirectAt(long offset, out int num, out int gen)
    {
        int p = (int)offset;
        num = int.Parse(ReadToken(ref p));
        gen = int.Parse(ReadToken(ref p));
        if (ReadToken(ref p) != "obj") throw new PdfParseException("obj keyword expected");
        var o = ParseObject(ref p);
        int q = SkipWs(p);
        if (o is PDict d && q + 6 <= _b.Length && Encoding.ASCII.GetString(_b, q, 6) == "stream")
        {
            q += 6;
            if (q < _b.Length && _b[q] == 13) q++;
            if (q < _b.Length && _b[q] == 10) q++;
            int len;
            var lenObj = d["Length"];
            if (lenObj is PRef lr) len = ((PNum)Resolve(lr)).Int;
            else len = ((PNum?)lenObj)?.Int ?? 0;
            if (len < 0 || q + len > _b.Length) len = FindEndStream(q) - q;
            var data = new byte[len];
            Array.Copy(_b, q, data, 0, len);
            return new PStream { Dict = d, Data = data };
        }
        return o;
    }

    private int FindEndStream(int from)
    {
        var pat = Encoding.ASCII.GetBytes("endstream");
        for (int i = from; i + pat.Length <= _b.Length; i++)
        {
            bool m = true;
            for (int k = 0; k < pat.Length && m; k++) m = _b[i + k] == pat[k];
            if (m)
            {
                int e = i;
                if (e > from && _b[e - 1] == 10) e--;
                if (e > from && _b[e - 1] == 13) e--;
                return e;
            }
        }
        return _b.Length;
    }

    public PObj Resolve(PObj? o)
    {
        if (o is not PRef r) return o ?? PNull.I;
        if (!_xref.TryGetValue(r.Num, out var e) || e.type == 0) return PNull.I;
        if (e.type == 1) return ParseIndirectAt(e.a, out _, out _);
        // compressed in an object stream
        int stmNum = (int)e.a;
        if (!_objStmCache.TryGetValue(stmNum, out var objs))
        {
            objs = new List<(int, PObj)>();
            var stm = Resolve(new PRef(stmNum, 0)) as PStream ?? throw new PdfParseException("Object stream missing");
            var data = Decode(stm);
            int n = ((PNum)stm.Dict["N"]!).Int, first = ((PNum)stm.Dict["First"]!).Int;
            var sub = new PdfMiniParser(data, headerOnly: true);
            int p = 0;
            var heads = new List<(int num, int off)>();
            for (int i = 0; i < n; i++)
            {
                int on = int.Parse(sub.ReadToken(ref p));
                int off = int.Parse(sub.ReadToken(ref p));
                heads.Add((on, off));
            }
            foreach (var (on, off) in heads)
            {
                int pp = first + off;
                objs.Add((on, sub.ParseObject(ref pp)));
            }
            _objStmCache[stmNum] = objs;
        }
        foreach (var (num, obj) in objs) if (num == r.Num) return obj;
        return PNull.I;
    }

    private PdfMiniParser(byte[] bytes, bool headerOnly) { _b = bytes; }

    // ------------------------------------------------------------- filters
    public static byte[] Decode(PStream s)
    {
        var filter = s.Dict["Filter"];
        string? f = filter switch
        {
            PName n => n.V,
            PArray a when a.Items.Count == 1 && a.Items[0] is PName n1 => n1.V,
            PArray a when a.Items.Count == 0 => null,
            null => null,
            _ => "unsupported",
        };
        if (f == null) return s.Data;
        if (f != "FlateDecode") throw new PdfParseException($"Unsupported filter {f}");
        byte[] raw;
        using (var z = new ZLibStream(new MemoryStream(s.Data), CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            try { z.CopyTo(ms); } catch (InvalidDataException) { /* keep what was decoded */ }
            raw = ms.ToArray();
        }
        var parms = s.Dict["DecodeParms"] as PDict ?? (s.Dict["DecodeParms"] as PArray)?.Items.FirstOrDefault() as PDict;
        int predictor = (parms?["Predictor"] as PNum)?.Int ?? 1;
        if (predictor < 10) return raw;
        int colors = (parms?["Colors"] as PNum)?.Int ?? 1;
        int bpc = (parms?["BitsPerComponent"] as PNum)?.Int ?? 8;
        int columns = (parms?["Columns"] as PNum)?.Int ?? 1;
        int bpp = Math.Max(1, colors * bpc / 8);
        int rowLen = (columns * colors * bpc + 7) / 8;
        var outp = new MemoryStream();
        var prev = new byte[rowLen];
        var row = new byte[rowLen];
        int pos = 0;
        while (pos + 1 + rowLen <= raw.Length)
        {
            int type = raw[pos++];
            Array.Copy(raw, pos, row, 0, rowLen);
            pos += rowLen;
            for (int i = 0; i < rowLen; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0;
                int up = prev[i];
                int ul = i >= bpp ? prev[i - bpp] : 0;
                row[i] = type switch
                {
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + (left + up) / 2),
                    4 => (byte)(row[i] + Paeth(left, up, ul)),
                    _ => row[i],
                };
            }
            outp.Write(row, 0, rowLen);
            (prev, row) = (row, prev);
        }
        return outp.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ------------------------------------------------------------- cross-reference
    private void LoadXref()
    {
        int tailStart = Math.Max(0, _b.Length - 2048);
        string tail = Encoding.Latin1.GetString(_b, tailStart, _b.Length - tailStart);
        int sx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (sx < 0) throw new PdfParseException("startxref not found");
        int p = tailStart + sx + 9;
        long off = long.Parse(ReadToken(ref p));
        LastXrefOffset = off;
        var visited = new HashSet<long>();
        bool first = true;
        while (off > 0 && off < _b.Length && visited.Add(off))
        {
            int q = SkipWs((int)off);
            bool isTable = q + 4 <= _b.Length && Encoding.ASCII.GetString(_b, q, 4) == "xref";
            PDict trailer;
            if (isTable) trailer = ReadXrefTable(q + 4);
            else
            {
                var obj = ParseIndirectAt(q, out _, out _) as PStream ?? throw new PdfParseException("xref stream expected");
                ReadXrefStream(obj);
                trailer = obj.Dict;
            }
            if (first)
            {
                Trailer = trailer;
                LastXrefIsStream = !isTable;
                Size = (trailer["Size"] as PNum)?.Int ?? 0;
                first = false;
            }
            if (isTable && trailer["XRefStm"] is PNum xs)
            {
                var xo = ParseIndirectAt(xs.Int, out _, out _) as PStream;
                if (xo != null) ReadXrefStream(xo);
            }
            off = (trailer["Prev"] as PNum)?.Int ?? 0;
        }
        if (Trailer["Root"] is not PRef) throw new PdfParseException("Document catalog not found");
    }

    private PDict ReadXrefTable(int p)
    {
        while (true)
        {
            int save = p;
            string t = ReadToken(ref p);
            if (t == "trailer") return (PDict)ParseObject(ref p);
            if (t.Length == 0 || !t.All(char.IsDigit)) { p = save; throw new PdfParseException("Bad xref table"); }
            int start = int.Parse(t);
            int count = int.Parse(ReadToken(ref p));
            for (int i = 0; i < count; i++)
            {
                long o = long.Parse(ReadToken(ref p));
                int g = int.Parse(ReadToken(ref p));
                string k = ReadToken(ref p);
                int num = start + i;
                if (!_xref.ContainsKey(num)) _xref[num] = (k == "n" ? 1 : 0, o, g);
            }
        }
    }

    private void ReadXrefStream(PStream s)
    {
        var data = Decode(s);
        var w = ((PArray)s.Dict["W"]!).Items.Select(x => ((PNum)x).Int).ToArray();
        int size = (s.Dict["Size"] as PNum)?.Int ?? 0;
        var index = (s.Dict["Index"] as PArray)?.Items.Select(x => ((PNum)x).Int).ToList() ?? new List<int> { 0, size };
        int rowLen = w.Sum();
        int pos = 0;
        for (int sec = 0; sec + 1 < index.Count; sec += 2)
        {
            int start = index[sec], count = index[sec + 1];
            for (int i = 0; i < count && pos + rowLen <= data.Length; i++)
            {
                long f1 = w[0] == 0 ? 1 : Field(data, pos, w[0]);
                long f2 = Field(data, pos + w[0], w[1]);
                long f3 = Field(data, pos + w[0] + w[1], w[2]);
                pos += rowLen;
                int num = start + i;
                if (!_xref.ContainsKey(num)) _xref[num] = ((int)f1, f2, (int)f3);
            }
        }
    }

    private static long Field(byte[] d, int p, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | d[p + i];
        return v;
    }

    public int GenerationOf(int num) => _xref.TryGetValue(num, out var e) && e.type == 1 ? e.b : 0;
}

public static class PdfSerializer
{
    public static void Write(StringBuilder sb, PObj o)
    {
        switch (o)
        {
            case PNull: sb.Append("null"); break;
            case PBool b: sb.Append(b.V ? "true" : "false"); break;
            case PNum n: sb.Append(n.Raw); break;
            case PRef r: sb.Append(r.Num).Append(' ').Append(r.Gen).Append(" R"); break;
            case PName nm: WriteName(sb, nm.V); break;
            case PStr s:
                sb.Append('<');
                foreach (var by in s.Bytes) sb.Append(by.ToString("X2"));
                sb.Append('>');
                break;
            case PArray a:
                sb.Append('[');
                for (int i = 0; i < a.Items.Count; i++) { if (i > 0) sb.Append(' '); Write(sb, a.Items[i]); }
                sb.Append(']');
                break;
            case PDict d:
                sb.Append("<<");
                foreach (var e in d.Entries) { WriteName(sb, e.Key); sb.Append(' '); Write(sb, e.Value); sb.Append(' '); }
                sb.Append(">>");
                break;
            default: throw new InvalidOperationException("Streams are not rewritten");
        }
    }

    private static void WriteName(StringBuilder sb, string name)
    {
        sb.Append('/');
        foreach (char c in name)
        {
            if (c > 32 && c < 127 && "()<>[]{}/%#".IndexOf(c) < 0) sb.Append(c);
            else sb.Append('#').Append(((int)c & 0xFF).ToString("X2"));
        }
    }

    public static PStr TextString(string s)
    {
        bool ascii = s.All(c => c >= 32 && c < 127);
        if (ascii) return new PStr(Encoding.ASCII.GetBytes(s));
        var b = new List<byte> { 0xFE, 0xFF };
        b.AddRange(Encoding.BigEndianUnicode.GetBytes(s));
        return new PStr(b.ToArray());
    }
}

/// <summary>Rewrites the outline (chapters + the app's "Bookmarks" folder) by appending an incremental update.</summary>
public static class OutlineWriter
{
    public const string BookmarksFolderTitle = "Bookmarks";

    /// <summary>Splits an outline read from a PDF into its chapters and the app's bookmark folder (last top-level item named "Bookmarks").</summary>
    public static (List<OutlineItem> chapters, List<OutlineItem> bookmarks) Split(List<OutlineItem> outline)
    {
        if (outline.Count > 0)
        {
            var last = outline[^1];
            if (last.Title == BookmarksFolderTitle && last.PageIndex < 0 && last.Uri == null)
                return (outline.Take(outline.Count - 1).ToList(), last.Children.ToList());
        }
        return (outline, new List<OutlineItem>());
    }

    public static List<OutlineItem> Combine(List<OutlineItem> chapters, List<OutlineItem> bookmarks)
    {
        var all = new List<OutlineItem>(chapters);
        if (bookmarks.Count > 0)
        {
            var folder = new OutlineItem { Title = BookmarksFolderTitle, IsOpen = true };
            foreach (var b in bookmarks) folder.Children.Add(new OutlineItem { Title = b.Title, PageIndex = b.PageIndex, PdfTop = b.PdfTop });
            all.Add(folder);
        }
        return all;
    }

    public static byte[] Apply(byte[] pdf, List<OutlineItem> roots)
    {
        var parser = new PdfMiniParser(pdf);
        if (parser.Trailer["Encrypt"] != null)
            throw new NotSupportedException("Bookmarks cannot be saved into encrypted PDFs.");
        var rootRef = (PRef)parser.Trailer["Root"]!;
        var catalog = parser.Resolve(rootRef) as PDict ?? throw new PdfParseException("Catalog is not a dictionary");

        // page references in order
        var pages = new List<PRef>();
        var visited = new HashSet<int>();
        void Walk(PObj node, int depth)
        {
            if (depth > 64) return;
            if (node is PRef r && !visited.Add(r.Num)) return;
            var d = parser.Resolve(node) as PDict;
            if (d == null) return;
            if (d["Kids"] is { } kidsObj && parser.Resolve(kidsObj) is PArray kids)
            {
                foreach (var k in kids.Items) Walk(k, depth + 1);
            }
            else if (node is PRef pr) pages.Add(pr);
        }
        Walk(catalog["Pages"] ?? PNull.I, 0);

        int next = Math.Max(parser.Size, 1);
        var objects = new List<(int num, int gen, string body)>();

        if (roots.Count == 0)
        {
            catalog["Outlines"] = null;
        }
        else
        {
            int outlineRoot = next++;
            var nums = new Dictionary<OutlineItem, int>();
            void Number(List<OutlineItem> items) { foreach (var i in items) { nums[i] = next++; Number(i.Children); } }
            Number(roots);

            static int Visible(OutlineItem i) => i.IsOpen ? i.Children.Sum(c => 1 + Visible(c)) : 0;

            void Emit(List<OutlineItem> items, int parent)
            {
                for (int k = 0; k < items.Count; k++)
                {
                    var it = items[k];
                    var d = new PDict();
                    d["Title"] = PdfSerializer.TextString(it.Title);
                    d["Parent"] = new PRef(parent, 0);
                    if (k > 0) d["Prev"] = new PRef(nums[items[k - 1]], 0);
                    if (k < items.Count - 1) d["Next"] = new PRef(nums[items[k + 1]], 0);
                    if (it.Children.Count > 0)
                    {
                        d["First"] = new PRef(nums[it.Children[0]], 0);
                        d["Last"] = new PRef(nums[it.Children[^1]], 0);
                        d["Count"] = new PNum(it.IsOpen ? Visible(it) : -it.Children.Count);
                    }
                    if (it.PageIndex >= 0 && it.PageIndex < pages.Count)
                    {
                        var dest = new PArray();
                        dest.Items.Add(pages[it.PageIndex]);
                        if (it.PdfTop is double top)
                        {
                            dest.Items.Add(new PName("XYZ"));
                            dest.Items.Add(PNull.I);
                            dest.Items.Add(new PNum(top));
                            dest.Items.Add(PNull.I);
                        }
                        else dest.Items.Add(new PName("Fit"));
                        d["Dest"] = dest;
                    }
                    else if (!string.IsNullOrEmpty(it.Uri))
                    {
                        var a = new PDict();
                        a["S"] = new PName("URI");
                        a["URI"] = new PStr(Encoding.ASCII.GetBytes(it.Uri));
                        d["A"] = a;
                    }
                    var sb = new StringBuilder();
                    PdfSerializer.Write(sb, d);
                    objects.Add((nums[it], 0, sb.ToString()));
                    Emit(it.Children, nums[it]);
                }
            }
            Emit(roots, outlineRoot);

            var od = new PDict();
            od["Type"] = new PName("Outlines");
            od["First"] = new PRef(nums[roots[0]], 0);
            od["Last"] = new PRef(nums[roots[^1]], 0);
            od["Count"] = new PNum(roots.Sum(r => 1 + Visible(r)));
            var osb = new StringBuilder();
            PdfSerializer.Write(osb, od);
            objects.Add((outlineRoot, 0, osb.ToString()));
            catalog["Outlines"] = new PRef(outlineRoot, 0);
        }

        var csb = new StringBuilder();
        PdfSerializer.Write(csb, catalog);
        objects.Add((rootRef.Num, rootRef.Gen, csb.ToString()));

        return IncrementalUpdate.Append(pdf, parser, rootRef, objects, next);
    }
}

/// <summary>Appends changed and new objects to a PDF as an incremental update: the original bytes stay as they are,
/// followed by the objects and a cross-reference section (a table or a stream, like the file's last one) that points
/// back to the previous one.</summary>
public static class IncrementalUpdate
{
    /// <param name="next">One more than the highest object number used by <paramref name="objects"/>.</param>
    public static byte[] Append(byte[] pdf, PdfMiniParser parser, PRef rootRef, List<(int num, int gen, string body)> objects, int next)
    {
        var outStream = new MemoryStream(pdf.Length + 16 * 1024);
        outStream.Write(pdf);
        void W(string s) => outStream.Write(Encoding.Latin1.GetBytes(s));
        if (pdf.Length > 0 && pdf[^1] != 10 && pdf[^1] != 13) W("\n");
        var offsets = new SortedDictionary<int, (long off, int gen)>();
        foreach (var (num, gen, body) in objects)
        {
            offsets[num] = (outStream.Position, gen);
            W($"{num} {gen} obj\n{body}\nendobj\n");
        }
        int newSize = Math.Max(parser.Size, next);

        var trailerExtras = new StringBuilder();
        trailerExtras.Append("/Root ").Append(rootRef.Num).Append(' ').Append(rootRef.Gen).Append(" R");
        if (parser.Trailer["Info"] is PRef info) trailerExtras.Append(" /Info ").Append(info.Num).Append(' ').Append(info.Gen).Append(" R");
        if (parser.Trailer["ID"] is PArray id) { trailerExtras.Append(" /ID "); PdfSerializer.Write(trailerExtras, id); }
        trailerExtras.Append(" /Prev ").Append(parser.LastXrefOffset);

        if (parser.LastXrefIsStream)
        {
            int xnum = newSize;
            newSize++;
            long xoff = outStream.Position;
            offsets[xnum] = (xoff, 0);
            var rows = new MemoryStream();
            var index = new StringBuilder();
            foreach (var (num, (off, gen)) in offsets)
            {
                index.Append(num).Append(" 1 ");
                rows.WriteByte(1);
                for (int s = 32; s >= 0; s -= 8) rows.WriteByte((byte)(off >> s)); // 5-byte offset
                rows.WriteByte((byte)(gen >> 8));
                rows.WriteByte((byte)gen);
            }
            var data = rows.ToArray();
            W($"{xnum} 0 obj\n<</Type /XRef /Size {newSize} /W [1 5 2] /Index [{index.ToString().Trim()}] {trailerExtras} /Length {data.Length}>>\nstream\n");
            outStream.Write(data);
            W($"\nendstream\nendobj\nstartxref\n{xoff}\n%%EOF\n");
        }
        else
        {
            long xoff = outStream.Position;
            W("xref\n0 1\n0000000000 65535 f\r\n");
            foreach (var (num, (off, gen)) in offsets)
                W($"{num} 1\n{off:0000000000} {gen:00000} n\r\n");
            W($"trailer\n<</Size {newSize} {trailerExtras}>>\nstartxref\n{xoff}\n%%EOF\n");
        }
        return outStream.ToArray();
    }
}
