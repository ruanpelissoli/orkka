namespace Orkka.LocalRunner.Runtime;

using System.Diagnostics;

/// <summary>Opens a URL in the person's default browser. Never throws.</summary>
internal static class Browser
{
    public static bool TryOpen(string url)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
                psi = new ProcessStartInfo(url) { UseShellExecute = true };
            else if (OperatingSystem.IsMacOS())
                psi = new ProcessStartInfo("open") { ArgumentList = { url }, UseShellExecute = false };
            else
                psi = new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false };
            using var process = Process.Start(psi);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
