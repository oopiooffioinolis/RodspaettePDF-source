using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Rodspaette.Core.Native;

namespace Rodspaette.Core;

/// <summary>
/// One open PDF. All methods must run on <see cref="PdfWorker"/> (use PdfWorker.Shared.Run(...)).
/// The file is loaded into memory, so the file on disk stays free to be overwritten by Save.
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    private sealed class PageEntry
    {
        public IntPtr Page;
        public IntPtr Text;
    }

    private byte[] _data;
    private GCHandle _pin;
    private IntPtr _doc;
    private readonly PageSize[] _sizes;
    private readonly PageTransform?[] _transforms;
    private readonly PageText?[] _texts;
    private readonly PageText?[] _ocrTexts;
    private readonly Dictionary<int, PageEntry> _open = new();
    private readonly LinkedList<int> _lru = new();
    private const int MaxOpenPages = 24;

    /// <summary>Incremented for a page whenever its annotations change, so cached bitmaps can be invalidated.</summary>
    public int[] PageRevision { get; }

    public int PageCount => _sizes.Length;
    public bool IsEncrypted { get; }
    public string? Password { get; }

    private PdfDocument(byte[] data, GCHandle pin, IntPtr doc, string? password)
    {
        _data = data; _pin = pin; _doc = doc; Password = password;
        int n = Pdfium.FPDF_GetPageCount(doc);
        _sizes = new PageSize[n];
        _transforms = new PageTransform?[n];
        _texts = new PageText?[n];
        _ocrTexts = new PageText?[n];
        PageRevision = new int[n];
        for (int i = 0; i < n; i++)
        {
            FS_SIZEF s;
            if (Pdfium.FPDF_GetPageSizeByIndexF(doc, i, &s) != 0 && s.Width > 0 && s.Height > 0)
                _sizes[i] = new PageSize(s.Width, s.Height);
            else
                _sizes[i] = new PageSize(612, 792);
        }
        IsEncrypted = password != null || ContainsEncrypt(data);
    }

    private static bool ContainsEncrypt(byte[] data)
    {
        // Cheap check: look for "/Encrypt" in the trailer area at the end of the file.
        int start = Math.Max(0, data.Length - 4096);
        var tail = Encoding.ASCII.GetString(data, start, data.Length - start);
        return tail.Contains("/Encrypt", StringComparison.Ordinal);
    }

    public static PdfDocument Open(byte[] data, string? password = null)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr doc = Pdfium.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)data.Length, password);
        if (doc == IntPtr.Zero)
        {
            pin.Free();
            uint err = (uint)Pdfium.FPDF_GetLastError().Value;
            throw err switch
            {
                4 => new PdfPasswordRequiredException(password != null),
                2 => new PdfOpenException("The file could not be found or opened."),
                3 => new PdfOpenException("This file is not a valid PDF, or it is damaged."),
                5 => new PdfOpenException("This PDF uses a security handler that is not supported."),
                _ => new PdfOpenException("The PDF could not be opened."),
            };
        }
        return new PdfDocument(data, pin, doc, password);
    }

    public PageSize GetPageSize(int index) => _sizes[index];

    // ------------------------------------------------------------------ pages

    private PageEntry GetPage(int index)
    {
        if (_doc == IntPtr.Zero) throw new ObjectDisposedException(nameof(PdfDocument));
        if (_open.TryGetValue(index, out var e))
        {
            _lru.Remove(index);
            _lru.AddFirst(index);
            return e;
        }
        IntPtr page = Pdfium.FPDF_LoadPage(_doc, index);
        if (page == IntPtr.Zero) throw new PdfOpenException($"Page {index + 1} could not be loaded.");
        e = new PageEntry { Page = page };
        _open[index] = e;
        _lru.AddFirst(index);
        while (_lru.Count > MaxOpenPages)
        {
            int victim = _lru.Last!.Value;
            _lru.RemoveLast();
            ClosePageEntry(_open[victim]);
            _open.Remove(victim);
        }
        return e;
    }

    private static void ClosePageEntry(PageEntry e)
    {
        if (e.Text != IntPtr.Zero) Pdfium.FPDFText_ClosePage(e.Text);
        if (e.Page != IntPtr.Zero) Pdfium.FPDF_ClosePage(e.Page);
        e.Text = IntPtr.Zero; e.Page = IntPtr.Zero;
    }

    private IntPtr GetTextPage(int index)
    {
        var e = GetPage(index);
        if (e.Text == IntPtr.Zero) e.Text = Pdfium.FPDFText_LoadPage(e.Page);
        return e.Text;
    }

    public PageTransform GetTransform(int index)
    {
        var t = _transforms[index];
        if (t != null) return t;
        var page = GetPage(index).Page;
        double w = _sizes[index].Width, h = _sizes[index].Height;
        const int K = 64;
        int sx = (int)Math.Round(w * K), sy = (int)Math.Round(h * K);
        Pdfium.FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, 0, 0, out double x00, out double y00);
        Pdfium.FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, sx, 0, out double x10, out double y10);
        Pdfium.FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, 0, sy, out double x01, out double y01);
        double vw = sx / (double)K, vh = sy / (double)K;
        t = new PageTransform((x10 - x00) / vw, (y10 - y00) / vw, (x01 - x00) / vh, (y01 - y00) / vh, x00, y00);
        _transforms[index] = t;
        return t;
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>
    /// Renders part of a page. <paramref name="scale"/> is device pixels per point.
    /// (x, y, w, h) is the region in device pixels of the page rendered at that scale.
    /// Returns BGRA pixels, stride = w*4, opaque.
    /// </summary>
    public byte[] Render(int index, double scale, int x, int y, int w, int h, bool annotations = true, bool printing = false, bool grayscale = false)
    {
        var page = GetPage(index).Page;
        var size = _sizes[index];
        int fullW = Math.Max(1, (int)Math.Round(size.Width * scale));
        int fullH = Math.Max(1, (int)Math.Round(size.Height * scale));
        w = Math.Max(1, w); h = Math.Max(1, h);
        var buffer = new byte[w * h * 4];
        fixed (byte* p = buffer)
        {
            IntPtr bmp = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.FPDFBitmap_BGRA, (IntPtr)p, w * 4);
            if (bmp == IntPtr.Zero) throw new OutOfMemoryException("Could not create render bitmap.");
            try
            {
                Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
                int flags = (annotations ? Pdfium.FPDF_ANNOT : 0) | (printing ? Pdfium.FPDF_PRINTING : 0) | (grayscale ? Pdfium.FPDF_GRAYSCALE : 0);
                Pdfium.FPDF_RenderPageBitmap(bmp, page, -x, -y, fullW, fullH, 0, flags);
            }
            finally { Pdfium.FPDFBitmap_Destroy(bmp); }
        }
        return buffer;
    }

    /// <summary>True when the page has no text layer but does contain images: a scan that needs OCR.</summary>
    public bool NeedsOcr(int index)
    {
        var text = GetPageText(index, includeOcr: false);
        if (text.Text.Count(c => !char.IsWhiteSpace(c)) >= 8) return false;
        var page = GetPage(index).Page;
        int n = Pdfium.FPDFPage_CountObjects(page);
        for (int i = 0; i < n; i++)
        {
            var obj = Pdfium.FPDFPage_GetObject(page, i);
            if (obj != IntPtr.Zero && Pdfium.FPDFPageObj_GetType(obj) == Pdfium.FPDF_PAGEOBJ_IMAGE) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ text

    public void SetOcrText(int index, PageText text) => _ocrTexts[index] = text;
    public bool HasOcrText(int index) => _ocrTexts[index] != null;

    /// <summary>The page's characters with boxes. Falls back to OCR text for scanned pages when available.</summary>
    public PageText GetPageText(int index, bool includeOcr = true)
    {
        var cached = _texts[index];
        if (cached == null)
        {
            var tp = GetTextPage(index);
            var transform = GetTransform(index);
            int n = Math.Max(0, Pdfium.FPDFText_CountChars(tp));
            var chars = new TextChar[n];
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                uint u = Pdfium.FPDFText_GetUnicode(tp, i);
                char c = u == 0 ? ' ' : u > 0xFFFF ? '�' : (char)u;
                if (c == '\r') c = '\n';
                var tc = new TextChar { Char = c };
                FS_RECTF r;
                if (Pdfium.FPDFText_GetLooseCharBox(tp, i, &r) != 0 && (r.Right - r.Left) > 0.01 && Math.Abs(r.Top - r.Bottom) > 0.01)
                {
                    tc.Box = transform.PdfRectToView(r.Left, r.Top, r.Right, r.Bottom);
                    tc.HasBox = true;
                }
                chars[i] = tc;
                sb.Append(c);
            }
            cached = new PageText { PageIndex = index, Chars = chars, Text = sb.ToString() };
            _texts[index] = cached;
        }
        if (includeOcr && cached.IsEmpty && _ocrTexts[index] is { } ocr) return ocr;
        return cached;
    }

    public List<SearchHit> SearchPage(int index, string query, bool matchCase, bool wholeWord)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        var text = GetPageText(index);
        if (text.FromOcr) return TextSearch.Find(text, query, matchCase, wholeWord, fromOcr: true);
        if (text.IsEmpty) return hits;

        var tp = GetTextPage(index);
        var transform = GetTransform(index);
        uint flags = (matchCase ? Pdfium.FPDF_MATCHCASE : 0) | (wholeWord ? Pdfium.FPDF_MATCHWHOLEWORD : 0);
        IntPtr h = Pdfium.FPDFText_FindStart(tp, query, new CULong(flags), 0);
        if (h == IntPtr.Zero) return hits;
        try
        {
            int guard = 0;
            while (Pdfium.FPDFText_FindNext(h) != 0 && guard++ < 10000)
            {
                int start = Pdfium.FPDFText_GetSchResultIndex(h);
                int count = Pdfium.FPDFText_GetSchCount(h);
                if (start < 0 || count <= 0) continue;
                var rects = new List<ViewRect>();
                int nr = Pdfium.FPDFText_CountRects(tp, start, count);
                for (int r = 0; r < nr; r++)
                {
                    if (Pdfium.FPDFText_GetRect(tp, r, out double l, out double t, out double rr, out double b) != 0)
                        rects.Add(transform.PdfRectToView(l, t, rr, b));
                }
                if (rects.Count == 0) rects.AddRange(text.GetRangeRects(start, count));
                hits.Add(TextSearch.MakeHit(text, start, count, rects, false));
            }
        }
        finally { Pdfium.FPDFText_FindClose(h); }
        return hits;
    }

    // ------------------------------------------------------------------ outline

    public List<OutlineItem> GetOutline()
    {
        var result = new List<OutlineItem>();
        int total = 0;
        ReadOutline(IntPtr.Zero, result, 0, ref total);
        return result;
    }

    private void ReadOutline(IntPtr parent, List<OutlineItem> into, int depth, ref int total)
    {
        if (depth > 32) return;
        var seen = new HashSet<IntPtr>();
        IntPtr bm = Pdfium.FPDFBookmark_GetFirstChild(_doc, parent);
        while (bm != IntPtr.Zero && total < 20000 && seen.Add(bm))
        {
            total++;
            var item = new OutlineItem { Depth = depth };
            item.Title = Pdfium.ReadUtf16((buf, len) => (int)Pdfium.FPDFBookmark_GetTitle(bm, (void*)buf, new CULong((uint)len)).Value).Trim();
            item.IsOpen = Pdfium.FPDFBookmark_GetCount(bm) > 0;

            IntPtr dest = Pdfium.FPDFBookmark_GetDest(_doc, bm);
            if (dest == IntPtr.Zero)
            {
                IntPtr action = Pdfium.FPDFBookmark_GetAction(bm);
                if (action != IntPtr.Zero)
                {
                    uint type = (uint)Pdfium.FPDFAction_GetType(action).Value;
                    if (type == Pdfium.PDFACTION_GOTO) dest = Pdfium.FPDFAction_GetDest(_doc, action);
                    else if (type == Pdfium.PDFACTION_URI)
                    {
                        uint need = (uint)Pdfium.FPDFAction_GetURIPath(_doc, action, null, new CULong(0)).Value;
                        if (need > 1)
                        {
                            var buf = new byte[need];
                            fixed (byte* p = buf) Pdfium.FPDFAction_GetURIPath(_doc, action, p, new CULong(need));
                            item.Uri = Encoding.UTF8.GetString(buf, 0, (int)need - 1);
                        }
                    }
                }
            }
            if (dest != IntPtr.Zero)
            {
                item.PageIndex = Pdfium.FPDFDest_GetDestPageIndex(_doc, dest);
                if (Pdfium.FPDFDest_GetLocationInPage(dest, out _, out int hasY, out _, out _, out float y, out _) != 0 && hasY != 0)
                    item.PdfTop = y;
            }
            into.Add(item);
            ReadOutline(bm, item.Children, depth + 1, ref total);
            bm = Pdfium.FPDFBookmark_GetNextSibling(_doc, bm);
        }
    }

    /// <summary>Converts a destination's PDF y (from the outline) to a view y on that page.</summary>
    public double PdfYToViewY(int index, double pdfY)
    {
        var t = GetTransform(index);
        // pick x at the page's left edge in PDF space
        var (px, _) = t.ViewToPdf(0, 0);
        return Math.Clamp(t.PdfToView(px, pdfY).y, 0, _sizes[index].Height);
    }

    // ------------------------------------------------------------------ annotations

    private void Touch(int index) => PageRevision[index]++;

    private static void SetCommon(IntPtr annot, RgbColor color, string? contents)
    {
        Pdfium.FPDFAnnot_SetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
        Pdfium.FPDFAnnot_SetFlags(annot, Pdfium.FPDF_ANNOT_FLAG_PRINT);
        Pdfium.FPDFAnnot_SetStringValue(annot, "T", Environment.UserName ?? "");
        Pdfium.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTime.Now));
        if (!string.IsNullOrEmpty(contents)) Pdfium.FPDFAnnot_SetStringValue(annot, "Contents", contents);
    }

    public static string PdfDate(DateTime t)
    {
        var off = TimeZoneInfo.Local.GetUtcOffset(t);
        string sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return $"D:{t:yyyyMMddHHmmss}{sign}{off.Hours:00}'{off.Minutes:00}'";
    }

    private int Finish(IntPtr page, IntPtr annot, int pageIndex)
    {
        int idx = Pdfium.FPDFPage_GetAnnotIndex(page, annot);
        Pdfium.FPDFPage_CloseAnnot(annot);
        Touch(pageIndex);
        return idx;
    }

    /// <summary>Adds a highlight / underline / strikeout over the given line rectangles (view units). Returns the annotation index.</summary>
    public int AddTextMarkup(int index, AnnotKind kind, IReadOnlyList<ViewRect> lines, RgbColor color)
    {
        if (lines.Count == 0) return -1;
        int subtype = kind switch
        {
            AnnotKind.Highlight => Pdfium.FPDF_ANNOT_HIGHLIGHT,
            AnnotKind.Underline => Pdfium.FPDF_ANNOT_UNDERLINE,
            AnnotKind.StrikeOut => Pdfium.FPDF_ANNOT_STRIKEOUT,
            AnnotKind.Squiggly => Pdfium.FPDF_ANNOT_SQUIGGLY,
            _ => throw new ArgumentException("Not a text markup kind", nameof(kind)),
        };
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        IntPtr annot = Pdfium.FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return -1;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var r in lines)
        {
            var (x1, y1) = t.ViewToPdf(r.X, r.Y);           // top-left
            var (x2, y2) = t.ViewToPdf(r.Right, r.Y);       // top-right
            var (x3, y3) = t.ViewToPdf(r.X, r.Bottom);      // bottom-left
            var (x4, y4) = t.ViewToPdf(r.Right, r.Bottom);  // bottom-right
            var q = new FS_QUADPOINTSF
            {
                X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2,
                X3 = (float)x3, Y3 = (float)y3, X4 = (float)x4, Y4 = (float)y4,
            };
            Pdfium.FPDFAnnot_AppendAttachmentPoints(annot, &q);
            foreach (var (x, y) in new[] { (x1, y1), (x2, y2), (x3, y3), (x4, y4) })
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        var rect = new FS_RECTF { Left = (float)minX, Top = (float)maxY, Right = (float)maxX, Bottom = (float)minY };
        Pdfium.FPDFAnnot_SetRect(annot, &rect);
        SetCommon(annot, color, null);
        return Finish(page, annot, index);
    }

    /// <summary>Adds a freehand ink annotation. Strokes are in view units; width in points.</summary>
    public int AddInk(int index, IReadOnlyList<IReadOnlyList<ViewPoint>> strokes, RgbColor color, float width)
    {
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        IntPtr annot = Pdfium.FPDFPage_CreateAnnot(page, Pdfium.FPDF_ANNOT_INK);
        if (annot == IntPtr.Zero) return -1;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var stroke in strokes)
        {
            if (stroke.Count == 0) continue;
            var pts = new FS_POINTF[stroke.Count];
            for (int i = 0; i < stroke.Count; i++)
            {
                var (x, y) = t.ViewToPdf(stroke[i].X, stroke[i].Y);
                pts[i] = new FS_POINTF { X = (float)x, Y = (float)y };
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            fixed (FS_POINTF* p = pts) Pdfium.FPDFAnnot_AddInkStroke(annot, p, (nuint)pts.Length);
        }
        float m = width / 2 + 1;
        var rect = new FS_RECTF { Left = (float)minX - m, Top = (float)maxY + m, Right = (float)maxX + m, Bottom = (float)minY - m };
        Pdfium.FPDFAnnot_SetRect(annot, &rect);
        Pdfium.FPDFAnnot_SetBorder(annot, 0, 0, width);
        SetCommon(annot, color, null);
        return Finish(page, annot, index);
    }

    /// <summary>Adds a sticky note icon at a point (view units).</summary>
    public int AddNote(int index, ViewPoint at, string text, RgbColor color)
    {
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        IntPtr annot = Pdfium.FPDFPage_CreateAnnot(page, Pdfium.FPDF_ANNOT_TEXT);
        if (annot == IntPtr.Zero) return -1;
        var (x, y) = t.ViewToPdf(at.X, at.Y);
        var rect = new FS_RECTF { Left = (float)x, Top = (float)y, Right = (float)x + 20, Bottom = (float)y - 20 };
        Pdfium.FPDFAnnot_SetRect(annot, &rect);
        Pdfium.FPDFAnnot_SetStringValue(annot, "Name", "Comment");
        SetCommon(annot, color, text);
        return Finish(page, annot, index);
    }

    /// <summary>
    /// Adds a text box. The appearance is supplied as glyph outlines (view units) so it looks identical in every reader
    /// without depending on fonts. The plain text is stored in /Contents.
    /// </summary>
    public int AddFreeText(int index, ViewRect box, string text, IReadOnlyList<PathCommand> glyphs, RgbColor color, double fontSize)
    {
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        IntPtr annot = Pdfium.FPDFPage_CreateAnnot(page, Pdfium.FPDF_ANNOT_FREETEXT);
        if (annot == IntPtr.Zero) return -1;
        var (l, top) = t.ViewToPdf(box.X, box.Y);
        var (r, bottom) = t.ViewToPdf(box.Right, box.Bottom);
        var rect = new FS_RECTF
        {
            Left = (float)Math.Min(l, r), Right = (float)Math.Max(l, r),
            Top = (float)Math.Max(top, bottom), Bottom = (float)Math.Min(top, bottom),
        };
        Pdfium.FPDFAnnot_SetRect(annot, &rect);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string da = string.Format(inv, "{0:0.###} {1:0.###} {2:0.###} rg /Helv {3:0.#} Tf", color.R / 255.0, color.G / 255.0, color.B / 255.0, fontSize);
        Pdfium.FPDFAnnot_SetStringValue(annot, "DA", da);
        Pdfium.FPDFAnnot_SetFlags(annot, Pdfium.FPDF_ANNOT_FLAG_PRINT);
        Pdfium.FPDFAnnot_SetStringValue(annot, "T", Environment.UserName ?? "");
        Pdfium.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTime.Now));
        Pdfium.FPDFAnnot_SetStringValue(annot, "Contents", text);
        string ap = PathCommand.ToPdfContent(glyphs, t, color);
        Pdfium.FPDFAnnot_SetAP(annot, Pdfium.FPDF_ANNOT_APPEARANCEMODE_NORMAL, ap);
        return Finish(page, annot, index);
    }

    public List<AnnotInfo> GetAnnotations(int index)
    {
        var list = new List<AnnotInfo>();
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        int n = Pdfium.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < n; i++)
        {
            IntPtr a = Pdfium.FPDFPage_GetAnnot(page, i);
            if (a == IntPtr.Zero) continue;
            try
            {
                int st = Pdfium.FPDFAnnot_GetSubtype(a);
                AnnotKind kind = st switch
                {
                    Pdfium.FPDF_ANNOT_HIGHLIGHT => AnnotKind.Highlight,
                    Pdfium.FPDF_ANNOT_UNDERLINE => AnnotKind.Underline,
                    Pdfium.FPDF_ANNOT_STRIKEOUT => AnnotKind.StrikeOut,
                    Pdfium.FPDF_ANNOT_SQUIGGLY => AnnotKind.Squiggly,
                    Pdfium.FPDF_ANNOT_INK => AnnotKind.Ink,
                    Pdfium.FPDF_ANNOT_TEXT => AnnotKind.Note,
                    Pdfium.FPDF_ANNOT_FREETEXT => AnnotKind.FreeText,
                    _ => AnnotKind.Other,
                };
                if (kind == AnnotKind.Other) continue; // links, popups, form fields: not shown in the markup list
                FS_RECTF r;
                ViewRect bounds = default;
                if (Pdfium.FPDFAnnot_GetRect(a, &r) != 0) bounds = t.PdfRectToView(r.Left, r.Top, r.Right, r.Bottom);
                uint argb;
                if (Pdfium.FPDFAnnot_GetColor(a, Pdfium.FPDFANNOT_COLORTYPE_Color, out uint cr, out uint cg, out uint cb, out _) != 0)
                    argb = 0xFF000000u | (cr << 16) | (cg << 8) | cb;
                else
                    argb = kind switch
                    {
                        AnnotKind.Highlight => 0xFFFFD60A,
                        AnnotKind.Note => 0xFFFFD60A,
                        AnnotKind.Underline or AnnotKind.StrikeOut or AnnotKind.Squiggly => 0xFFD9480F,
                        _ => 0xFF3A3A3C,
                    };
                string contents = Pdfium.ReadUtf16((buf, len) => (int)Pdfium.FPDFAnnot_GetStringValue(a, "Contents", (void*)buf, new CULong((uint)len)).Value);
                string marked = "";
                if (kind is AnnotKind.Highlight or AnnotKind.Underline or AnnotKind.StrikeOut or AnnotKind.Squiggly)
                    marked = GetMarkedText(index, a, t);
                double fontSize = 0;
                uint textArgb = 0;
                if (kind == AnnotKind.FreeText)
                    (fontSize, textArgb) = ParseDefaultAppearance(Pdfium.ReadUtf16((buf, len) => (int)Pdfium.FPDFAnnot_GetStringValue(a, "DA", (void*)buf, new CULong((uint)len)).Value));
                list.Add(new AnnotInfo { PageIndex = index, Index = i, Kind = kind, Bounds = bounds, ColorArgb = argb, Contents = contents, MarkedText = marked, FontSize = fontSize, TextArgb = textArgb });
            }
            finally { Pdfium.FPDFPage_CloseAnnot(a); }
        }
        return list;
    }

    private string GetMarkedText(int index, IntPtr annot, PageTransform t)
    {
        var text = GetPageText(index);
        if (text.IsEmpty) return "";
        var quads = new List<ViewRect>();
        nuint n = Pdfium.FPDFAnnot_CountAttachmentPoints(annot);
        for (nuint i = 0; i < n && i < 500; i++)
        {
            FS_QUADPOINTSF q;
            if (Pdfium.FPDFAnnot_GetAttachmentPoints(annot, i, &q) == 0) continue;
            double l = Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4));
            double r = Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4));
            double b = Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4));
            double tp = Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4));
            quads.Add(t.PdfRectToView(l, tp, r, b));
        }
        if (quads.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var c in text.Chars)
        {
            if (!c.HasBox) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            double cx = c.Box.X + c.Box.Width / 2, cy = c.Box.Y + c.Box.Height / 2;
            foreach (var q in quads)
                if (q.Contains(cx, cy)) { sb.Append(c.Char == '\n' ? ' ' : c.Char); break; }
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public bool RemoveAnnotation(int index, int annotIndex)
    {
        var page = GetPage(index).Page;
        bool ok = Pdfium.FPDFPage_RemoveAnnot(page, annotIndex) != 0;
        if (ok) Touch(index);
        return ok;
    }

    /// <summary>Text size and colour from a default-appearance string such as "0.2 0.3 0.4 rg /Helv 12 Tf" (0 where missing).</summary>
    public static (double Size, uint Argb) ParseDefaultAppearance(string da)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var style = System.Globalization.NumberStyles.Float;
        double size = 0;
        uint argb = 0;
        var tf = System.Text.RegularExpressions.Regex.Match(da, @"/\S+\s+([\d.]+)\s+Tf");
        if (tf.Success) double.TryParse(tf.Groups[1].Value, style, inv, out size);
        static byte B(string v) => (byte)Math.Clamp(Math.Round(double.Parse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) * 255), 0, 255);
        var rg = System.Text.RegularExpressions.Regex.Match(da, @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg\b");
        if (rg.Success)
            argb = 0xFF000000u | ((uint)B(rg.Groups[1].Value) << 16) | ((uint)B(rg.Groups[2].Value) << 8) | B(rg.Groups[3].Value);
        else
        {
            var g = System.Text.RegularExpressions.Regex.Match(da, @"(?<![\w.])([\d.]+)\s+g\b");
            if (g.Success) { uint v = B(g.Groups[1].Value); argb = 0xFF000000u | (v << 16) | (v << 8) | v; }
        }
        return (size, argb);
    }

    private static FS_RECTF ToPdfRect(PageTransform t, ViewRect box)
    {
        var (l, top) = t.ViewToPdf(box.X, box.Y);
        var (r, bottom) = t.ViewToPdf(box.Right, box.Bottom);
        return new FS_RECTF
        {
            Left = (float)Math.Min(l, r), Right = (float)Math.Max(l, r),
            Top = (float)Math.Max(top, bottom), Bottom = (float)Math.Min(top, bottom),
        };
    }

    /// <summary>Moves an annotation by (dx, dy) view units. Only its /Rect changes: readers draw its appearance in the
    /// new place, so it looks exactly as before (whichever app made it).</summary>
    public bool MoveAnnotation(int index, int annotIndex, double dx, double dy)
    {
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        IntPtr a = Pdfium.FPDFPage_GetAnnot(page, annotIndex);
        if (a == IntPtr.Zero) return false;
        try
        {
            FS_RECTF r;
            if (Pdfium.FPDFAnnot_GetRect(a, &r) == 0) return false;
            var v = t.PdfRectToView(r.Left, r.Top, r.Right, r.Bottom);
            var rect = ToPdfRect(t, new ViewRect(v.X + dx, v.Y + dy, v.Width, v.Height));
            if (Pdfium.FPDFAnnot_SetRect(a, &rect) == 0) return false;
            Pdfium.FPDFAnnot_SetStringValue(a, "M", PdfDate(DateTime.Now));
            return true;
        }
        finally { Pdfium.FPDFPage_CloseAnnot(a); Touch(index); }
    }

    /// <summary>Changes a text box: its rectangle, its text and its look (glyph outlines, as in <see cref="AddFreeText"/>).
    /// Its size and colour setting (/DA) stay as they are.</summary>
    public bool UpdateFreeText(int index, int annotIndex, ViewRect box, string text, IReadOnlyList<PathCommand> glyphs, RgbColor color)
    {
        var page = GetPage(index).Page;
        var t = GetTransform(index);
        IntPtr a = Pdfium.FPDFPage_GetAnnot(page, annotIndex);
        if (a == IntPtr.Zero) return false;
        try
        {
            if (Pdfium.FPDFAnnot_GetSubtype(a) != Pdfium.FPDF_ANNOT_FREETEXT) return false;
            var rect = ToPdfRect(t, box);
            if (Pdfium.FPDFAnnot_SetRect(a, &rect) == 0) return false;
            Pdfium.FPDFAnnot_SetStringValue(a, "Contents", text);
            Pdfium.FPDFAnnot_SetStringValue(a, "M", PdfDate(DateTime.Now));
            // set after the rectangle: the new appearance takes the new rectangle as its bounding box
            return Pdfium.FPDFAnnot_SetAP(a, Pdfium.FPDF_ANNOT_APPEARANCEMODE_NORMAL, PathCommand.ToPdfContent(glyphs, t, color)) != 0;
        }
        finally { Pdfium.FPDFPage_CloseAnnot(a); Touch(index); }
    }

    public bool SetAnnotationContents(int index, int annotIndex, string text)
    {
        var page = GetPage(index).Page;
        IntPtr a = Pdfium.FPDFPage_GetAnnot(page, annotIndex);
        if (a == IntPtr.Zero) return false;
        try { return Pdfium.FPDFAnnot_SetStringValue(a, "Contents", text) != 0; }
        finally { Pdfium.FPDFPage_CloseAnnot(a); Touch(index); }
    }

    // ------------------------------------------------------------------ saving

    [ThreadStatic] private static MemoryStream? _saveStream;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteBlock(FPDF_FILEWRITE* self, IntPtr data, CULong size)
    {
        try
        {
            int n = (int)size.Value;
            _saveStream!.Write(new ReadOnlySpan<byte>((void*)data, n));
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Each page's label from the PDF's own page numbering (/PageLabels), or null when it has none.</summary>
    public string[]? PageLabels()
    {
        var labels = new string[PageCount];
        bool any = false;
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            labels[i] = Pdfium.ReadUtf16((buf, len) => (int)Pdfium.FPDF_GetPageLabel(_doc, idx, (void*)buf, new CULong((uint)len)).Value);
            if (labels[i].Length > 0) any = true;
        }
        return any ? labels : null;
    }

    /// <summary>The PDF's page numbering as ranges for editing, read from the file (null: it has none).
    /// Throws <see cref="NotSupportedException"/> for encrypted PDFs.</summary>
    public List<PageLabelRange>? PageLabelRanges() => PageNumbering.Read(_data);

    /// <summary>Serialises the document (with all annotation changes) to a complete new PDF file.</summary>
    public byte[] SaveToBytes()
    {
        _saveStream = new MemoryStream(_data.Length + 64 * 1024);
        try
        {
            var fw = new FPDF_FILEWRITE { Version = 1, WriteBlock = &WriteBlock };
            const uint FPDF_NO_INCREMENTAL = 2;
            if (Pdfium.FPDF_SaveAsCopy(_doc, &fw, FPDF_NO_INCREMENTAL) == 0)
                throw new IOException("PDFium could not write the document.");
            return _saveStream.ToArray();
        }
        finally { _saveStream = null; }
    }

    public void Dispose()
    {
        foreach (var e in _open.Values) ClosePageEntry(e);
        _open.Clear();
        _lru.Clear();
        if (_doc != IntPtr.Zero) { Pdfium.FPDF_CloseDocument(_doc); _doc = IntPtr.Zero; }
        if (_pin.IsAllocated) _pin.Free();
        _data = Array.Empty<byte>();
    }
}
