using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Rodspaette.App;

/// <summary>
/// "Update app": builds the source folder with dotnet publish while the app keeps running,
/// then a small script waits for the app to close, copies the new build over the installed one and restarts it.
/// </summary>
public static class Updater
{
    private const string Csproj = @"src\Rodspaette.App\Rodspaette.App.csproj";

    public static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>The source folder (the one with build.cmd), or null if it can't be found.</summary>
    public static string? FindSource(AppSettings s)
    {
        if (Normalize(s.SourceFolder) is { } fromSettings) return fromSettings;
        try
        {
            var marker = Path.Combine(InstallDir, "source-path.txt");
            if (File.Exists(marker) && Normalize(File.ReadAllText(marker).Trim()) is { } fromInstall) return fromInstall;
        }
        catch { }
        var desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RodspaettePDF-source");
        return Normalize(desk);
    }

    /// <summary>Accepts the source folder itself or the folder the zip was unzipped into.</summary>
    public static string? Normalize(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        foreach (var d in new[] { dir, Path.Combine(dir, "rodspaette") })
            if (File.Exists(Path.Combine(d, Csproj))) return Path.GetFullPath(d).TrimEnd('\\');
        return null;
    }

    /// <summary>Where the build goes: the source's publish folder, unless the app is running from it.</summary>
    public static string OutputDir(string source)
    {
        var dir = Path.Combine(source, "publish");
        return string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(source, "publish-next") : dir;
    }

    public static async Task<(bool ok, string output)> Build(string source, string outDir)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = source,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "publish", Csproj, "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-o", outDir, "-nologo" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, (await stdout) + (await stderr));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "The .NET 8 SDK wasn't found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and try again.");
        }
    }

    /// <summary>The error lines of a failed build, short enough for a dialog.</summary>
    public static string Summarize(string output)
    {
        var errors = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .Select(l => { int i = l.IndexOf(" [", StringComparison.Ordinal); return i > 0 ? l[..i] : l; })
            .Select(l => { int i = l.IndexOf(@"src\", StringComparison.OrdinalIgnoreCase); return i > 0 ? l[i..] : l; })
            .Distinct().Take(8).ToList();
        if (errors.Count > 0) return string.Join("\n", errors);
        var tail = output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(8);
        return string.Join("\n", tail);
    }

    /// <summary>True when the fresh build is byte-for-byte what is already installed.</summary>
    public static bool SameAsInstalled(string outDir)
    {
        try
        {
            foreach (var f in new[] { "RodspaettePDF.dll", "Rodspaette.Core.dll" })
            {
                var a = Path.Combine(outDir, f);
                var b = Path.Combine(InstallDir, f);
                if (!File.Exists(a) || !File.Exists(b)) return false;
                if (!SHA256.HashData(File.ReadAllBytes(a)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(b)))) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts the hidden script that swaps in the new build once this process has exited, then reopens the files.</summary>
    public static void LaunchSwap(string source, string outDir, IReadOnlyList<string> reopen)
    {
        var temp = Path.Combine(Path.GetTempPath(), "rodspaette-update");
        Directory.CreateDirectory(temp);
        var script = Path.Combine(temp, "swap.ps1");
        var list = Path.Combine(temp, "reopen.txt");
        File.WriteAllText(script, SwapScript, new UTF8Encoding(true));
        File.WriteAllLines(list, reopen, new UTF8Encoding(true));

        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                                  "-AppPid", Environment.ProcessId.ToString(), "-Src", outDir, "-Dest", InstallDir,
                                  "-Source", source, "-ListFile", list })
            psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private const string SwapScript = """
        param([int]$AppPid, [string]$Src, [string]$Dest, [string]$Source, [string]$ListFile)
        $log = Join-Path (Split-Path $ListFile) 'swap.log'
        "$(Get-Date -Format s) waiting for $AppPid" | Out-File $log
        try { Wait-Process -Id $AppPid -Timeout 60 -ErrorAction Stop } catch { }
        $copied = $false
        for ($i = 0; $i -lt 30 -and -not $copied; $i++) {
            try { Copy-Item -Path (Join-Path $Src '*') -Destination $Dest -Recurse -Force -ErrorAction Stop; $copied = $true }
            catch { "$(Get-Date -Format s) retry: $($_.Exception.Message)" | Out-File $log -Append; Start-Sleep -Milliseconds 500 }
        }
        try { Set-Content -LiteralPath (Join-Path $Dest 'source-path.txt') -Value $Source -Encoding UTF8 } catch { }
        "$(Get-Date -Format s) copied=$copied" | Out-File $log -Append
        $exe = Join-Path $Dest 'RodspaettePDF.exe'
        $files = @()
        if (Test-Path -LiteralPath $ListFile) { $files = @(Get-Content -LiteralPath $ListFile -Encoding UTF8 | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) }
        if ($files.Count -gt 0) { Start-Process -FilePath $exe -ArgumentList ($files | ForEach-Object { '"' + $_ + '"' }) }
        else { Start-Process -FilePath $exe }
        """;
}
