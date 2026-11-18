// gh.exe for Windows: the runner starts `gh` with Process.Start and UseShellExecute=false,
// which (like CreateProcess) only ever finds an .exe — a gh.cmd on PATH is invisible to it.
// This shim forwards its arguments to `node gh.mjs`, inheriting stdin/stdout/stderr, and
// exits with node's exit code. Two lines in gh.shim.txt next to it name node and the script.
// Compiled by src/fakes/stub-gh.ts with the .NET Framework csc that ships with Windows.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class GhShim
{
    private static int Main(string[] args)
    {
        var here = AppDomain.CurrentDomain.BaseDirectory;
        var config = File.ReadAllLines(Path.Combine(here, "gh.shim.txt"));
        var node = config[0].Trim();
        var script = config[1].Trim();

        var line = new StringBuilder(Quote(script));
        foreach (var a in args)
            line.Append(' ').Append(Quote(a));

        var psi = new ProcessStartInfo(node, line.ToString()) { UseShellExecute = false };
        using (var p = Process.Start(psi))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    // CommandLineToArgvW rules: backslashes are literal unless they precede a quote.
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            return arg;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', backslashes); sb.Append(arg[i]); }
        }
        return sb.Append('"').ToString();
    }
}
