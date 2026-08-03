using Microsoft.Extensions.Logging;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;

namespace ModSync.Server.Test;

// Satisfies ISptLogger<T> without writing anywhere.
// Tests care about behaviour, not log output.
//
// SPT 4.1 reshaped this interface: it moved to SPTarkov.Common.Models.Logging, LogLevel is now
// Microsoft's rather than SPT's own copy, and the colour parameters are Spectre.Console.Color
// instead of the deleted LogTextColor/LogBackgroundColor enums. DumpAndStop is no longer part
// of the interface, so it's gone too.
internal sealed class NoOpLogger<T> : ISptLogger<T>
{
    public void Debug(string message, Exception? exception = null) { }
    public void Info(string message, Exception? exception = null) { }
    public void Warning(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
    public void Critical(string message, Exception? exception = null) { }
    public void Success(string message, Exception? exception = null) { }
    public void Log(LogLevel level, string message, Color? textColor = null, Color? backgroundColor = null, Exception? exception = null) { }
    public void LogWithColor(string message, Color? textColor = null, Color? backgroundColor = null, Exception? exception = null) { }
    public bool IsLogEnabled(LogLevel level) => false;
}
