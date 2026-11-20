using Rodspaette.Core;

// Smoke tests for the PDF engine. Run on Linux with libpdfium.so next to the binary,
// or on Windows with pdfium.dll. Usage: CoreTests <data dir> <out dir>
string data = args.Length > 0 ? args[0] : "../data";
string outDir = args.Length > 1 ? args[1] : "out";
Directory.CreateDirectory(outDir);
var W = PdfWorker.Shared;
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) failures++; }
void SaveRaw(string name, byte[] bgra, int w, int h) => File.WriteAllBytes(Path.Combine(outDir, $"{name}_{w}x{h}.bgra"), bgra);

foreach (var file in new[] { "sample.pdf", "sample_objstm.pdf", "sample_rot.pdf" })
{
    Console.WriteLine($"== {file}");
    var bytes = File.ReadAllBytes(Path.Combine(data, file));
    var doc = await W.Run(() => PdfDocument.Open(bytes));
    Check(doc.PageCount == 7, $"page count {doc.PageCount}");
    var s0 = doc.GetPageSize(0);
    Check(Math.Abs(s0.Width - 595.27) < 1 && Math.Abs(s0.Height - 841.89) < 1, $"A4 size {s0.Width:0.##}x{s0.Height:0.##}");
    if (file == "sample_rot.pdf")
    {
        var s1 = doc.GetPageSize(1);
        Check(s1.Width > s1.Height, $"rotated page is landscape {s1.Width:0}x{s1.Height:0}");
    }

    // render
    double scale = 1.5;
    int w = (int)Math.Round(s0.Width * scale), h = (int)Math.Round(s0.Height * scale);
    var px = await W.Run(() => doc.Render(0, scale, 0, 0, w, h));
    int dark = 0; for (int i = 0; i < px.Length; i += 4) if (px[i] < 128) dark++;
    Check(dark > 1000, $"page 1 renders with ink ({dark} dark px)");
    if (file == "sample.pdf") SaveRaw("page1", px, w, h);
    var part = await W.Run(() => doc.Render(0, 4.0, 200, 300, 400, 200));
    Check(part.Length == 400 * 200 * 4, "region render at 400%");

    // text
    var text = await W.Run(() => doc.GetPageText(0));
    Check(text.Text.Contains("zero-point energy"), "text layer has 'zero-point energy'");
    Check(text.Text.Contains("Rødspætte"), "Danish characters survive");
    int idx = text.Text.IndexOf("ground state", StringComparison.Ordinal);
    var rects = text.GetRangeRects(idx, "ground state".Length);
    Check(rects.Count == 1 && rects[0].Width > 40, $"selection rect for 'ground state' {rects.FirstOrDefault()}");
    var r0 = rects[0];
    int hit = text.HitTest(r0.X + 2, r0.Y + r0.Height / 2);
    Check(hit == idx, $"hit test finds the 'g' (got {hit}, want {idx})");
    var (ws, wc) = text.WordAt(idx + 2);
    Check(text.Text.Substring(ws, wc) == "ground", "double-click word = 'ground'");

    // search
    var hits = new List<SearchHit>();
    for (int p = 0; p < doc.PageCount; p++) { int pp = p; hits.AddRange(await W.Run(() => doc.SearchPage(pp, "zero-point", false, false))); }
    Check(hits.Count >= 6, $"search 'zero-point' finds {hits.Count} hits");
    Check(hits.All(x => x.Rects.Count > 0), "every hit has rectangles");
    Console.WriteLine($"    e.g. p{hits[0].PageIndex + 1}: …{hits[0].Before}[{hits[0].Match}]{hits[0].After}");
    var caseHits = await W.Run(() => doc.SearchPage(1, "ZERO-POINT", true, false));
    Check(caseHits.Count == 1, $"match case finds exactly 1 on page 2 (got {caseHits.Count})");
    var wordHits = await W.Run(() => doc.SearchPage(1, "Lecture", false, true));
    var partHits = await W.Run(() => doc.SearchPage(1, "Lect", false, true));
    Check(wordHits.Count == 1 && partHits.Count == 0, "whole word option");

    // outline
    var outline = await W.Run(() => doc.GetOutline());
    void Dump(List<OutlineItem> items, string ind) { foreach (var o in items) { Console.WriteLine($"    {ind}{o.Title} -> p{o.PageIndex + 1} top={o.PdfTop}"); Dump(o.Children, ind + "  "); } }
    Dump(outline, "");
    Check(outline.Count == 1 && outline[0].Children.Count == 3, "outline: 1 chapter with 3 sections");

    // OCR detection
    bool scan = await W.Run(() => doc.NeedsOcr(2));
    bool notScan = await W.Run(() => doc.NeedsOcr(0));
    Check(scan && !notScan, "page 3 detected as scanned, page 1 not");

    // OCR text layer
    var words = new List<OcrWord> {
        new("PROBLEM", new ViewRect(72, 96, 80, 20), 0), new("SET", new ViewRect(156, 96, 30, 20), 0),
        new("Compute", new ViewRect(72, 144, 70, 20), 1), new("ladder", new ViewRect(150, 144, 60, 20), 1), new("operators.", new ViewRect(214, 144, 80, 20), 1) };
    var ocr = OcrText.FromWords(2, words);
    await W.Run(() => doc.SetOcrText(2, ocr));
    var ocrHits = await W.Run(() => doc.SearchPage(2, "ladder op", false, false));
    Check(ocrHits.Count == 1 && ocrHits[0].FromOcr && ocrHits[0].Rects.Count == 1, "search finds OCR text 'ladder op'");

    if (file != "sample.pdf") { await W.Run(() => doc.Dispose()); continue; }

    // annotations
    int hl = await W.Run(() => doc.AddTextMarkup(0, AnnotKind.Highlight, rects, RgbColor.FromHex("FFD60A")));
    var idx2 = text.Text.IndexOf("uncertainty principle", StringComparison.Ordinal);
    int ul = await W.Run(() => doc.AddTextMarkup(0, AnnotKind.Underline, text.GetRangeRects(idx2, 21), RgbColor.FromHex("D9480F")));
    int so = await W.Run(() => doc.AddTextMarkup(0, AnnotKind.StrikeOut, text.GetRangeRects(text.Text.IndexOf("forces", StringComparison.Ordinal), 6), RgbColor.FromHex("E5341F")));
    int hlOcr = await W.Run(() => doc.AddTextMarkup(2, AnnotKind.Highlight, ocrHits[0].Rects, RgbColor.FromHex("34C759")));
    var stroke = Enumerable.Range(0, 40).Select(i => new ViewPoint(300 + i * 4, 450 + 15 * Math.Sin(i / 4.0))).ToList();
    int ink = await W.Run(() => doc.AddInk(0, new[] { (IReadOnlyList<ViewPoint>)stroke }, RgbColor.FromHex("E5341F"), 2f));
    int note = await W.Run(() => doc.AddNote(0, new ViewPoint(520, 120), "Ask the TA about part 3", RgbColor.FromHex("FFD60A")));
    var glyph = new List<PathCommand> { PathCommand.Move(80, 500), PathCommand.Line(180, 500), PathCommand.Line(180, 512), PathCommand.Line(80, 512), PathCommand.Close() };
    int ft = await W.Run(() => doc.AddFreeText(0, new ViewRect(76, 490, 120, 30), "Text box", glyph, RgbColor.FromHex("1D1D1F"), 12));
    Check(hl >= 0 && ul > hl && so > ul && ink > so && note > ink && ft > note && hlOcr >= 0, $"annotations created ({hl},{ul},{so},{ink},{note},{ft}; ocr {hlOcr})");
    var annots = await W.Run(() => doc.GetAnnotations(0));
    Check(annots.Count == 6, $"page 1 lists 6 annotations ({string.Join(", ", annots.Select(a => a.Kind))})");
    var hlInfo = annots.First(a => a.Kind == AnnotKind.Highlight);
    Check(hlInfo.MarkedText == "ground state", $"highlight marked text = '{hlInfo.MarkedText}'");
    Check(annots.First(a => a.Kind == AnnotKind.Note).Contents == "Ask the TA about part 3", "note contents");
    var px2 = await W.Run(() => doc.Render(0, scale, 0, 0, w, h));
    SaveRaw("page1_annots", px2, w, h);
    int yellow = 0; for (int i = 0; i < px2.Length; i += 4) if (px2[i] < 60 && px2[i + 1] > 180 && px2[i + 2] > 220) yellow++;
    Check(yellow > 200, $"highlight visible in render ({yellow} yellow px)");

    // remove + re-add (undo)
    bool removed = await W.Run(() => doc.RemoveAnnotation(0, so));
    var after = await W.Run(() => doc.GetAnnotations(0));
    Check(removed && after.Count == 5 && after.All(a => a.Kind != AnnotKind.StrikeOut), "remove strikeout");

    // tints
    var tinted = (byte[])px2.Clone(); PageTint.ApplyDark(tinted);
    Check(tinted[0] < 60 && tinted[1] < 60, $"dark tint turns paper dark ({tinted[2]},{tinted[1]},{tinted[0]})");
    var green = (byte[])px2.Clone(); PageTint.ApplyMultiply(green, RgbColor.FromHex("C7EDCC"));
    Check(green[2] == 0xC7 && green[1] == 0xED && green[0] == 0xCC, "green tint paper = #C7EDCC");
    SaveRaw("page1_dark", tinted, w, h);
    SaveRaw("page1_green", green, w, h);

    // save + reopen
    var saved = await W.Run(() => doc.SaveToBytes());
    File.WriteAllBytes(Path.Combine(outDir, "saved.pdf"), saved);
    var doc2 = await W.Run(() => PdfDocument.Open(saved));
    var annots2 = await W.Run(() => doc2.GetAnnotations(0));
    Check(annots2.Count == 5, $"annotations survive save ({annots2.Count})");
    var annotsP3 = await W.Run(() => doc2.GetAnnotations(2));
    Check(annotsP3.Count == 1 && annotsP3[0].Kind == AnnotKind.Highlight, "OCR highlight saved on scanned page");
    var px3 = await W.Run(() => doc2.Render(0, scale, 0, 0, w, h));
    SaveRaw("page1_reopened", px3, w, h);

    // bookmarks: add to chapters, write, reopen
    var (chapters, bms) = OutlineWriter.Split(await W.Run(() => doc2.GetOutline()));
    Check(bms.Count == 0, "no bookmark folder yet");
    var t5 = await W.Run(() => doc2.GetTransform(4));
    var (_, pdfTop) = t5.ViewToPdf(0, 200);
    bms.Add(new OutlineItem { Title = "Coherent states — derivation (Rødspætte)", PageIndex = 4, PdfTop = pdfTop });
    bms.Add(new OutlineItem { Title = "Problem set", PageIndex = 2 });
    var withBm = OutlineWriter.Apply(saved, OutlineWriter.Combine(chapters, bms));
    File.WriteAllBytes(Path.Combine(outDir, "bookmarked.pdf"), withBm);
    var doc3 = await W.Run(() => PdfDocument.Open(withBm));
    var (ch3, bm3) = OutlineWriter.Split(await W.Run(() => doc3.GetOutline()));
    Check(ch3.Count == 1 && ch3[0].Children.Count == 3, "chapters preserved after bookmark save");
    Check(bm3.Count == 2 && bm3[0].Title == "Coherent states — derivation (Rødspætte)" && bm3[0].PageIndex == 4, $"bookmarks read back: {string.Join(" | ", bm3.Select(b => $"{b.Title} p{b.PageIndex + 1} top={b.PdfTop:0.#}"))}");
    double vy = await W.Run(() => doc3.PdfYToViewY(4, bm3[0].PdfTop ?? 0));
    Check(Math.Abs(vy - 200) < 1, $"bookmark position round-trips (view y {vy:0.#})");
    var annots3 = await W.Run(() => doc3.GetAnnotations(0));
    Check(annots3.Count == 5, "annotations still there after bookmark update");

    // remove all bookmarks again
    var noBm = OutlineWriter.Apply(withBm, OutlineWriter.Combine(ch3, new List<OutlineItem>()));
    var doc4 = await W.Run(() => PdfDocument.Open(noBm));
    var (ch4, bm4) = OutlineWriter.Split(await W.Run(() => doc4.GetOutline()));
    Check(bm4.Count == 0 && ch4.Count == 1, "bookmark folder removed when empty");
    File.WriteAllBytes(Path.Combine(outDir, "bookmarks_removed.pdf"), noBm);
    foreach (var d in new[] { doc, doc2, doc3, doc4 }) await W.Run(() => d.Dispose());
}

// Outline writer directly on a file with cross-reference streams + object streams
{
    Console.WriteLine("== outline writer on xref-stream file");
    var bytes = File.ReadAllBytes(Path.Combine(data, "sample_objstm.pdf"));
    var parser = new PdfMiniParser(bytes);
    Check(parser.LastXrefIsStream, "file uses an xref stream");
    var doc = await W.Run(() => PdfDocument.Open(bytes));
    var (ch, bm) = OutlineWriter.Split(await W.Run(() => doc.GetOutline()));
    bm.Add(new OutlineItem { Title = "Mark on page 6", PageIndex = 5 });
    var outBytes = OutlineWriter.Apply(bytes, OutlineWriter.Combine(ch, bm));
    File.WriteAllBytes(Path.Combine(outDir, "objstm_bookmarked.pdf"), outBytes);
    var d2 = await W.Run(() => PdfDocument.Open(outBytes));
    var (ch2, bm2) = OutlineWriter.Split(await W.Run(() => d2.GetOutline()));
    Check(bm2.Count == 1 && bm2[0].PageIndex == 5 && ch2.Count == 1, "bookmark written via xref stream update");
    Check(d2.PageCount == 7, "still 7 pages");
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
