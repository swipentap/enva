namespace Enva.Libs;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public static class LogLevelExtensions
{
    public static string ToString(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };
    }
}
