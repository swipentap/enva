using System;
using System.IO;
using System.Text;
using System.Diagnostics;

namespace Enva.Libs;

public class Logger
{
    private const string DefaultLoggerName = "enva";
    private const string LogSeparator = "==================================================";
    
    private static Logger? defaultLogger;
    private static FileStream? logFile;
    private static LogLevel logLevel;
    
    private readonly string name;
    private readonly TextWriter writer;
    private readonly LogLevel level;

    private Logger(string name, TextWriter writer, LogLevel level)
    {
        this.name = name;
        this.writer = writer;
        this.level = level;
    }

    public static Logger InitLogger(LogLevel level, string logFilePath, bool alwaysLogToFile)
    {
        FileStream? fileStream = null;
        
        if (string.IsNullOrEmpty(logFilePath) && alwaysLogToFile)
        {
            // Create logs directory
            var logsDir = "logs";
            Directory.CreateDirectory(logsDir);
            
            // Create timestamped log file
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            logFilePath = Path.Combine(logsDir, $"enva_{timestamp}.log");
        }

        var writers = new List<TextWriter> { Console.Out };
        
        if (!string.IsNullOrEmpty(logFilePath))
        {
            try
            {
                fileStream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                var fileWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
                writers.Add(fileWriter);
                logFile = fileStream;
            }
            catch (Exception ex)
            {
                throw new Exception($"failed to open log file: {ex.Message}");
            }
        }

        var multiWriter = new MultiTextWriter(writers);
        var logger = new Logger(DefaultLoggerName, multiWriter, level);
        defaultLogger = logger;
        logLevel = level;

        return logger;
    }

    public static Logger GetLogger(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            name = DefaultLoggerName;
        }
        
        if (defaultLogger == null)
        {
            // Fallback logger if not initialized
            return new Logger(name, Console.Out, LogLevel.Info);
        }
        
        return new Logger(name, defaultLogger.writer, logLevel);
    }

    public static Logger GetDefaultLogger()
    {
        if (defaultLogger == null)
        {
            defaultLogger = new Logger(DefaultLoggerName, Console.Out, LogLevel.Info);
        }
        return defaultLogger;
    }

    public static void CloseLogFile()
    {
        logFile?.Close();
        logFile = null;
    }

    private string FormatMessage(LogLevel level, string format, params object[] args)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var message = args.Length > 0 ? string.Format(format, args) : format;
        var levelStr = level.ToString().ToUpper();
        // Format: timestamp - logger_name (25 chars, left-aligned) - level - message
        return $"{timestamp} - {name,-25} - {levelStr} - {message}\n";
    }

    public void Info(string format, params object[] args)
    {
        if (level <= LogLevel.Info)
        {
            var msg = FormatMessage(LogLevel.Info, format, args);
            writer.Write(msg);
        }
    }

    public void Error(string format, params object[] args)
    {
        if (level <= LogLevel.Error)
        {
            var msg = FormatMessage(LogLevel.Error, format, args);
            writer.Write(msg);
        }
    }

    public void Warning(string format, params object[] args)
    {
        if (level <= LogLevel.Warning)
        {
            var msg = FormatMessage(LogLevel.Warning, format, args);
            writer.Write(msg);
        }
    }

    public void Debug(string format, params object[] args)
    {
        if (level <= LogLevel.Debug)
        {
            var msg = FormatMessage(LogLevel.Debug, format, args);
            writer.Write(msg);
        }
    }

    public void Printf(string format, params object[] args)
    {
        Info(format, args);
    }

    public void Print(params object[] args)
    {
        Info("{0}", string.Join("", args));
    }

    public void InfoBanner(string message)
    {
        Info(LogSeparator);
        Info(message);
        Info(LogSeparator);
    }

    public void InfoBannerf(string format, params object[] args)
    {
        Info(LogSeparator);
        Info(format, args);
        Info(LogSeparator);
    }

    public void InfoBannerStart()
    {
        Info(LogSeparator);
    }

    public void InfoBannerEnd()
    {
        Info(LogSeparator);
    }

    public void LogTraceback(Exception err)
    {
        Error("Error: {0}", err);
        // Print stack trace
        var stackTrace = new StackTrace(err, true);
        var frames = stackTrace.GetFrames();
        if (frames != null)
        {
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method != null)
                {
                    var fileName = frame.GetFileName();
                    var lineNumber = frame.GetFileLineNumber();
                    var methodName = method.Name;
                    var className = method.DeclaringType?.FullName ?? "";
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        Error("  at {0}.{1} in {2}:line {3}", className, methodName, fileName, lineNumber);
                    }
                    else
                    {
                        Error("  at {0}.{1}", className, methodName);
                    }
                }
            }
        }
    }
}

internal class MultiTextWriter : TextWriter
{
    private readonly List<TextWriter> writers;

    public MultiTextWriter(List<TextWriter> writers)
    {
        this.writers = writers;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        foreach (var writer in writers)
        {
            writer.Write(value);
        }
    }

    public override void Write(string? value)
    {
        foreach (var writer in writers)
        {
            writer.Write(value);
        }
    }

    public override void Flush()
    {
        foreach (var writer in writers)
        {
            writer.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var writer in writers)
            {
                writer.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
