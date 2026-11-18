namespace Orkka.LocalRunner.Cli;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

/// <summary>
/// Default-mode console formatter: level prefix + message, nothing else. No category,
/// event id, timestamp, or stack trace — the runner terminal is end-user UX and must
/// not expose internal type names or file paths. `--debug` switches to the full
/// structured SimpleConsole output instead.
/// </summary>
public sealed class CleanConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "orkka-clean";

    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            return;

        textWriter.Write(logEntry.LogLevel switch
        {
            LogLevel.Warning => "warning: ",
            LogLevel.Error or LogLevel.Critical => "error: ",
            _ => "",
        });
        textWriter.Write(message);
        if (logEntry.Exception is not null)
        {
            // Message only — exception type names and stack frames leak internals.
            textWriter.Write(" — ");
            textWriter.Write(logEntry.Exception.Message);
        }
        textWriter.WriteLine();
    }
}
