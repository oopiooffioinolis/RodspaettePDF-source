using System.Collections.Concurrent;
using Rodspaette.Core.Native;

namespace Rodspaette.Core;

/// <summary>
/// PDFium is not thread-safe. Every PDFium call in the app goes through this single worker thread.
/// Work items are small (one page render, one page search) so the UI never waits long.
/// Two queues: interactive work (rendering what is on screen) always runs before background work (OCR, search).
/// </summary>
public sealed class PdfWorker : IDisposable
{
    private static readonly Lazy<PdfWorker> _shared = new(() => new PdfWorker());
    public static PdfWorker Shared => _shared.Value;

    private readonly BlockingCollection<Action> _high = new();
    private readonly BlockingCollection<Action> _low = new();
    private readonly Thread _thread;
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _disposed;

    public int ThreadId => _thread.ManagedThreadId;
    public bool IsOnWorker => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    private PdfWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "PDFium worker" };
        _thread.Start();
        Run(() => Pdfium.FPDF_InitLibrary()).Wait();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_high.TryTake(out var a) || _low.TryTake(out a))
            {
                try { a(); } catch { /* individual tasks report their own exceptions */ }
            }
        }
    }

    public Task Run(Action action, bool background = false) => Run<object?>(() => { action(); return null; }, background);

    public Task<T> Run<T>(Func<T> func, bool background = false)
    {
        if (IsOnWorker) // re-entrant call: run inline
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        (background ? _low : _high).Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _signal.Release();
        return tcs.Task;
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Release();
    }
}
