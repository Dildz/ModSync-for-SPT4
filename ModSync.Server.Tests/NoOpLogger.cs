using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;

namespace ModSync.Server.Test;

// Satisfies ISptLogger<T> without writing anywhere.
// Tests care about behaviour, not log output.
internal sealed class NoOpLogger<T> : ISptLogger<T>
{
    public void Debug(string message, Exception? exception = null) { }
    public void Info(string message, Exception? exception = null) { }
    public void Warning(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
    public void Critical(string message, Exception? exception = null) { }
    public void Success(string message, Exception? exception = null) { }
    public void Log(LogLevel level, string message, LogTextColor? color = null, LogBackgroundColor? backgroundColor = null, Exception? exception = null) { }
    public void LogWithColor(string message, LogTextColor? color = null, LogBackgroundColor? backgroundColor = null, Exception? exception = null) { }
    public bool IsLogEnabled(LogLevel level) => false;
    public void DumpAndStop() { }
}
