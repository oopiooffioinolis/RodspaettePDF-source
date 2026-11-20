using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Rodspaette.Core;

namespace Rodspaette.App;

/// <summary>One open document: the PDF, its view, bookmarks, undo history and OCR state.</summary>
public sealed class DocumentTab : INotifyPropertyChanged
{
    public PdfDocument Doc { get; private set; }
    public string FilePath { get; private set; }
    public string Name => Path.GetFileName(FilePath);
    public PdfViewer Viewer { get; }

    public List<OutlineItem> Chapters { get; set; } = new();
    public List<OutlineItem> Bookmarks { get; set; } = new();
    public bool BookmarksDirty { get; set; }

    /// <summary>What each page is called (the PDF's page numbering), or null for plain 1, 2, 3 ….</summary>
    public string[]? LabelTexts { get; private set; }
    /// <summary>The numbering set in Page numbers… that the open copy of the PDF doesn't have yet (null: the file's own).</summary>
    public List<PageLabelRange>? Labels { get; private set; }
    /// <summary>Page numbering changed and not yet saved.</summary>
    public bool LabelsDirty { get; set; }
    /// <summary>The file has page numbers the open copy doesn't (an autosave wrote them without reopening): keep writing them.</summary>
    public bool LabelsPending { get; set; }

    /// <summary>Annotations created this session, newest last: (page, annotation index). Used by Undo.</summary>
    public List<(int Page, int Index)> UndoStack { get; } = new();

    /// <summary>OCR results per page, kept so they can be re-attached after the document is reloaded on save.</summary>
    public Dictionary<int, List<OcrWord>> OcrWords { get; } = new();
    public CancellationTokenSource OcrCancel { get; } = new();

    private bool _dirty;
    /// <summary>Goes up on every edit, so a save can tell whether something changed while it was writing.</summary>
    public int EditVersion { get; set; }
    /// <summary>The file has bookmarks the open copy doesn't (an autosave wrote them without reopening): keep writing them.</summary>
    public bool OutlinePending { get; set; }
    public bool Dirty { get => _dirty; set { if (_dirty != value) { _dirty = value; Changed(); Changed(nameof(Meta)); } } }

    private bool _isActive;
    public bool IsActive { get => _isActive; set { if (_isActive != value) { _isActive = value; Changed(); } } }

    private int _currentPage;
    public int CurrentPage { get => _currentPage; set { if (_currentPage != value) { _currentPage = value; Changed(); Changed(nameof(Meta)); } } }

    private string _ocrStatus = "";
    public string OcrStatus { get => _ocrStatus; set { if (_ocrStatus != value) { _ocrStatus = value; Changed(); Changed(nameof(Meta)); } } }

    public string Meta
    {
        get
        {
            var s = LabelTexts != null ? $"Page {LabelOf(CurrentPage)} ({CurrentPage + 1} of {Doc.PageCount})" : $"Page {CurrentPage + 1} of {Doc.PageCount}";
            if (Dirty) s += " · edited";
            if (!string.IsNullOrEmpty(OcrStatus)) s += " · " + OcrStatus;
            return s;
        }
    }

    public IntPtr ProxyHwnd { get; set; }

    public DocumentTab(PdfDocument doc, string path, AppSettings settings)
    {
        Doc = doc;
        FilePath = path;
        Viewer = new PdfViewer(this, settings);
    }

    public void ReplaceDocument(PdfDocument doc, string path)
    {
        Doc = doc;
        if (!string.Equals(FilePath, path, StringComparison.Ordinal)) { FilePath = path; Changed(nameof(FilePath)); Changed(nameof(Name)); }
        UndoStack.Clear();
        Changed(nameof(Meta));
    }

    /// <summary>The page numbering as the PDF itself has it (after opening, or after a save reopened the file).</summary>
    public void LoadedLabels(string[]? labels)
    {
        LabelTexts = labels;
        Labels = null;
        Changed(nameof(Meta));
    }

    /// <summary>New page numbering from Page numbers…; it goes into the file with the next save.</summary>
    public void SetLabels(List<PageLabelRange> ranges)
    {
        var pages = PageNumbering.Expand(ranges, Doc.PageCount);
        Labels = ranges;
        LabelTexts = PageNumbering.IsPlain(pages) ? null : pages.Select(p => p.Label).ToArray();
        LabelsDirty = true;
        Changed(nameof(Meta));
    }

    /// <summary>What page <paramref name="index"/> is called: its label, or its plain number.</summary>
    public string LabelOf(int index) =>
        LabelTexts != null && index >= 0 && index < LabelTexts.Length && LabelTexts[index].Length > 0 ? LabelTexts[index] : (index + 1).ToString();

    /// <summary>The page a typed page number means: a page label (exactly, then ignoring case), then a plain page
    /// number. "(14)" always means the 14th page of the file. -1 if there is no such page.</summary>
    public int FindPage(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return -1;
        if (text.Length > 2 && text[0] == '(' && text[^1] == ')' && int.TryParse(text[1..^1], out int physical))
            return physical >= 1 && physical <= Doc.PageCount ? physical - 1 : -1;
        if (LabelTexts != null)
        {
            int i = Array.IndexOf(LabelTexts, text);
            if (i >= 0) return i;
            for (i = 0; i < LabelTexts.Length; i++)
                if (string.Equals(LabelTexts[i], text, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return int.TryParse(text, out int n) && n >= 1 && n <= Doc.PageCount ? n - 1 : -1;
    }

    /// <summary>Keeps undo indices valid after an annotation is removed from a page.</summary>
    public void OnAnnotationRemoved(int page, int index)
    {
        for (int i = UndoStack.Count - 1; i >= 0; i--)
        {
            var (p, idx) = UndoStack[i];
            if (p != page) continue;
            if (idx == index) UndoStack.RemoveAt(i);
            else if (idx > index) UndoStack[i] = (p, idx - 1);
        }
    }

    public OutlineItem? ChapterAt(int page)
    {
        OutlineItem? best = null;
        void Walk(List<OutlineItem> items)
        {
            foreach (var it in items)
            {
                if (it.PageIndex >= 0 && it.PageIndex <= page && (best == null || it.PageIndex >= best.PageIndex)) best = it;
                Walk(it.Children);
            }
        }
        Walk(Chapters);
        return best;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
