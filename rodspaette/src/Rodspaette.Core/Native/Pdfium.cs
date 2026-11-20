using System.Runtime.InteropServices;

namespace Rodspaette.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct FS_SIZEF { public float Width; public float Height; }

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF { public float Left; public float Top; public float Right; public float Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct FS_POINTF { public float X; public float Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct FS_QUADPOINTSF
{
    public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEWRITE
{
    public int Version;
    public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, IntPtr, CULong, int> WriteBlock;
}

/// <summary>
/// Thin P/Invoke layer over PDFium (pdfium.dll on Windows, libpdfium.so on Linux for tests).
/// Every call must happen on the single PDFium worker thread: PDFium is not thread-safe.
/// C "unsigned long" is 32-bit on Windows and 64-bit on Linux, so it is mapped to CULong.
/// </summary>
internal static unsafe partial class Pdfium
{
    private const string Lib = "pdfium";

    // ---------- library / document ----------
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDF_DestroyLibrary();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern CULong FPDF_GetLastError();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr doc, int index, FS_SIZEF* size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDF_SaveAsCopy(IntPtr doc, FPDF_FILEWRITE* fileWrite, uint flags);

    // ---------- page ----------
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, out double pageX, out double pageY);

    // ---------- rendering ----------
    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_GRAYSCALE = 0x08;
    public const int FPDF_PRINTING = 0x800;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ---------- text ----------
    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, ushort* result);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, FS_RECTF* rect);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_CountRects(IntPtr textPage, int start, int count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_GetRect(IntPtr textPage, int index, out double left, out double top, out double right, out double bottom);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFText_FindStart(IntPtr textPage, [MarshalAs(UnmanagedType.LPWStr)] string findWhat, CULong flags, int startIndex);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_FindNext(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_GetSchResultIndex(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFText_GetSchCount(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDFText_FindClose(IntPtr handle);

    // ---------- outline ----------
    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr doc, IntPtr bookmark);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr doc, IntPtr bookmark);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern CULong FPDFBookmark_GetTitle(IntPtr bookmark, void* buffer, CULong bufLen);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern CULong FPDF_GetPageLabel(IntPtr document, int pageIndex, void* buffer, CULong bufLen);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFBookmark_GetCount(IntPtr bookmark);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr doc, IntPtr bookmark);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern CULong FPDFAction_GetType(IntPtr action);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern CULong FPDFAction_GetURIPath(IntPtr doc, IntPtr action, void* buffer, CULong bufLen);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFDest_GetLocationInPage(IntPtr dest, out int hasX, out int hasY, out int hasZoom, out float x, out float y, out float zoom);

    // ---------- annotations ----------
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_IsSupportedSubtype(int subtype);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFPage_GetAnnotIndex(IntPtr page, IntPtr annot);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, FS_QUADPOINTSF* quad);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint index, FS_QUADPOINTSF* quad);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_SetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_GetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_GetFlags(IntPtr annot);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float hRadius, float vRadius, float width);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* points, nuint count);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern CULong FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, void* buffer, CULong bufLen);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetAP(IntPtr annot, int appearanceMode, [MarshalAs(UnmanagedType.LPWStr)] string value);

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_FREETEXT = 3;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_UNDERLINE = 10;
    public const int FPDF_ANNOT_SQUIGGLY = 11;
    public const int FPDF_ANNOT_STRIKEOUT = 12;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_POPUP = 16;
    public const int FPDF_ANNOT_WIDGET = 20;

    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;

    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_IMAGE = 3;

    public static string ReadUtf16(Func<IntPtr, int, int> getter)
    {
        // getter(buffer, bufLenBytes) returns required length in bytes including the terminator
        int len = getter(IntPtr.Zero, 0);
        if (len <= 2) return string.Empty;
        var buf = new byte[len];
        fixed (byte* p = buf) getter((IntPtr)p, len);
        int chars = len / 2 - 1;
        return System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, chars * 2));
    }
}
