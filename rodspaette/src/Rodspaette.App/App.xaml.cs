using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Rodspaette.App;

public partial class App : Application
{
    private SingleInstance? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = e.Args
            .Where(a => !string.IsNullOrWhiteSpace(a) && File.Exists(a))
            .Select(a => Path.GetFullPath(a))
            .ToArray();

        // one window, one taskbar icon: hand files to the running instance if there is one
        _single = new SingleInstance();
        if (!_single.IsFirst && SingleInstance.SendToFirst(paths))
        {
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        Theme.TintPages = settings.TintPages;
        Theme.Apply(settings.Theme);

        DispatcherUnhandledException += OnUnhandled;

        var win = new MainWindow(settings);
        MainWindow = win;
        win.Show();

        _single.Listen(p => Dispatcher.InvokeAsync(() => win.OpenFromOtherInstance(p)));
        FileAssociation.RegisterOpenWith();

        win.OpenAtStartup(paths);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ErrorLog.Write("unhandled", e.Exception);
        Dialogs.Info(MainWindow, "Something went wrong", e.Exception.Message);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
