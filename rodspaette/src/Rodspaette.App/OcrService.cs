using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rodspaette.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using OcrWord = Rodspaette.Core.OcrWord;

namespace Rodspaette.App;

/// <summary>
/// Background text recognition with the OCR engine built into Windows (offline).
/// Only pages without a text layer that contain images are processed.
/// </summary>
public static class OcrService
{
    private static OcrEngine? _engine;
    private static bool _triedCreate;

    private static OcrEngine? Engine
    {
        get
        {
            if (_triedCreate) return _engine;
            _triedCreate = true;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null)
                {
                    foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                    {
                        _engine = OcrEngine.TryCreateFromLanguage(lang);
                        if (_engine != null) break;
                    }
                }
            }
            catch { _engine = null; }
            return _engine;
        }
    }

    public static bool Available => Engine != null;

    /// <summary>Runs OCR over every scanned page of a tab. Reports progress and each finished page.</summary>
    public static async Task RunAsync(DocumentTab tab, Action<int> pageDone, Action<string> status)
    {
        var token = tab.OcrCancel.Token;
        var engine = Engine;
        if (engine == null) return;
        var doc = tab.Doc;
        int n = doc.PageCount;
        var todo = new List<int>();
        for (int i = 0; i < n && !token.IsCancellationRequested; i++)
        {
            int page = i;
            bool needs;
            try { needs = await PdfWorker.Shared.Run(() => doc.NeedsOcr(page), background: true); }
            catch { return; }
            if (needs && !tab.OcrWords.ContainsKey(page)) todo.Add(page);
        }
        if (todo.Count == 0 || token.IsCancellationRequested) return;

        int done = 0;
        foreach (int page in todo)
        {
            if (token.IsCancellationRequested) return;
            status($"recognising text {done + 1}/{todo.Count}");
            try
            {
                doc = tab.Doc; // may have been replaced by a save
                var size = doc.GetPageSize(page);
                double scale = Math.Min(300.0 / 72.0, (OcrEngine.MaxImageDimension - 2) / Math.Max(size.Width, size.Height));
                int w = Math.Max(1, (int)(size.Width * scale)), h = Math.Max(1, (int)(size.Height * scale));
                var px = await PdfWorker.Shared.Run(() => doc.Render(page, scale, 0, 0, w, h, annotations: false), background: true);
                var words = await Recognize(engine, px, w, h, scale);
                if (token.IsCancellationRequested) return;
                tab.OcrWords[page] = words;
                var text = OcrText.FromWords(page, words);
                var current = tab.Doc;
                await PdfWorker.Shared.Run(() => current.SetOcrText(page, text), background: true);
                pageDone(page);
            }
            catch { /* skip pages that fail */ }
            done++;
        }
        status("");
    }

    private static async Task<List<OcrWord>> Recognize(OcrEngine engine, byte[] bgra, int w, int h, double scale)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(bgra);
        using var color = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        using var gray = SoftwareBitmap.Convert(color, BitmapPixelFormat.Gray8);
        var result = await engine.RecognizeAsync(gray);
        var words = new List<OcrWord>();
        int lineId = 0;
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWord(word.Text, new ViewRect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale), lineId));
            }
            lineId++;
        }
        return words;
    }

    /// <summary>Re-attaches stored OCR results to a freshly loaded document (after saving).</summary>
    public static async Task Reattach(DocumentTab tab)
    {
        var doc = tab.Doc;
        foreach (var (page, words) in tab.OcrWords.ToList())
        {
            var text = OcrText.FromWords(page, words);
            await PdfWorker.Shared.Run(() => doc.SetOcrText(page, text));
        }
    }
}
