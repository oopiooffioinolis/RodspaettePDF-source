using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Networking.Connectivity;

namespace Rodspaette.App;

/// <summary>
/// Whether the PC is online: Windows' own answer (the one behind the taskbar's "No internet" icon), kept up to date
/// as networks come and go. Some networks block Windows' check while the internet works, so before anything is
/// refused, <see cref="ConfirmOnlineAsync"/> asks Google itself.
/// </summary>
public sealed class Connectivity
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private bool _windowsOnline = true, _reachedGoogle;

    public bool IsOnline => _windowsOnline || _reachedGoogle;

    /// <summary>Raised on the UI thread when <see cref="IsOnline"/> changes.</summary>
    public event Action? Changed;

    public void Start(Dispatcher ui)
    {
        Refresh();
        try { NetworkInformation.NetworkStatusChanged += _ => ui.BeginInvoke(Refresh); }
        catch { }
    }

    private void Refresh()
    {
        bool was = IsOnline;
        _windowsOnline = WindowsSaysOnline();
        _reachedGoogle = false;   // a network change: Windows' answer counts again
        if (IsOnline != was) Changed?.Invoke();
    }

    private static bool WindowsSaysOnline()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile != null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
        catch { return true; }   // can't tell: don't claim to be offline
    }

    /// <summary>True if online. When Windows says offline but a network is connected, tries Google (2 s at most).</summary>
    public async Task<bool> ConfirmOnlineAsync()
    {
        if (IsOnline) return true;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return false;
        try
        {
            using var r = await Http.GetAsync("https://www.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead);
            if ((int)r.StatusCode >= 400) return false;
        }
        catch { return false; }
        _reachedGoogle = true;
        Changed?.Invoke();
        return true;
    }
}

/// <summary>
/// "Tool bar transparency" while typing: typing in the tool bar or in Look up keeps them solid, even with the mouse
/// away, until a second after the last key. The mouse leaving them makes them see-through again straight away.
/// </summary>
public sealed class TypingGlass
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public bool Active { get; private set; }

    public event Action? Changed;

    public TypingGlass()
    {
        _timer.Tick += (_, _) => { _timer.Stop(); Set(false); };
    }

    /// <summary>A key was typed in the tool bar or in Look up.</summary>
    public void Typed()
    {
        _timer.Stop();
        _timer.Start();
        Set(true);
    }

    /// <summary>The mouse left them.</summary>
    public void MouseLeft()
    {
        _timer.Stop();
        Set(false);
    }

    private void Set(bool on)
    {
        if (Active == on) return;
        Active = on;
        Changed?.Invoke();
    }
}
