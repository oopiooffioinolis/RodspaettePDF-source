using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rodspaette.App;

/// <summary>
/// Keeps one Rødspætte window: opening another PDF (double-click in Explorer) sends the path
/// to the running instance, which adds it as a tab. One window = one taskbar icon.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = "RodspaettePDF." + Environment.UserName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, "Local\\" + Id, out bool created);
        IsFirst = created;
    }

    /// <summary>Sends paths to the first instance. Returns true when delivered.</summary>
    public static bool SendToFirst(string[] paths)
    {
        try
        {
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            using var w = new StreamWriter(client, Encoding.UTF8);
            w.WriteLine(paths.Length == 0 ? "<activate>" : string.Join("\n", paths));
            w.WriteLine("<end>");
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Listen(Action<string[]> onPaths)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var r = new StreamReader(server, Encoding.UTF8);
                    var list = new System.Collections.Generic.List<string>();
                    string? line;
                    while ((line = await r.ReadLineAsync()) != null && line != "<end>")
                        if (line != "<activate>" && line.Length > 0) list.Add(line);
                    onPaths(list.ToArray());
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
