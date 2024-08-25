using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

public class SystemdLogFormatter : ConsoleFormatter
{
    public SystemdLogFormatter() : base("SystemdLogFormatter") { }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider scopeProvider, TextWriter textWriter)
    {
        string message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string logLevel = GetShortLogLevel(logEntry.LogLevel);

        textWriter.Write($"{timestamp} <{logLevel}> {message}");

        if (logEntry.Exception != null)
        {
            textWriter.WriteLine();
            textWriter.Write(logEntry.Exception.ToString());
        }

        textWriter.WriteLine();
    }

    private static string GetShortLogLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}
