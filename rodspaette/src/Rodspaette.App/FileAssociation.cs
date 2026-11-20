using System;
using Microsoft.Win32;

namespace Rodspaette.App;

/// <summary>
/// Registers Rødspætte PDF for the current user (no admin rights) so it shows up in
/// "Open with" for PDFs and can be picked as the default PDF app in Windows Settings.
/// Windows does not let apps make themselves the default; the user chooses it once.
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "RodspaettePDF.Document";

    public static void RegisterOpenWith()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return;
            string command = $"\"{exe}\" \"%1\"";

            using (var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
                if (existing?.GetValue("") as string == command) return; // already up to date

            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", "PDF document");
                prog.SetValue("FriendlyTypeName", "PDF document");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            }
            using (var owp = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
                owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\RodspaettePDF.exe"))
            {
                app.SetValue("FriendlyAppName", "Rødspætte PDF");
                using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
                using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            }
            // Registered application capabilities, so it is listed under Settings > Apps > Default apps
            using (var caps = Registry.CurrentUser.CreateSubKey(@"Software\RodspaettePDF\Capabilities"))
            {
                caps.SetValue("ApplicationName", "Rødspætte PDF");
                caps.SetValue("ApplicationDescription", "A light PDF reader");
                using (var fa = caps.CreateSubKey("FileAssociations")) fa.SetValue(".pdf", ProgId);
            }
            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue("Rødspætte PDF", @"Software\RodspaettePDF\Capabilities");

            Win32.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }
        catch { /* registration is a convenience; never block startup */ }
    }
}
