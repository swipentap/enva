package libs

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

const (
	defaultLoggerName = "enva"
	logSeparator      = "=================================================="
)

var (
	defaultLogger *Logger
	logFile       *os.File
	logLevel      LogLevel
)

// LogLevel represents logging level
type LogLevel int

const (
	LogLevelDebug LogLevel = iota
	LogLevelInfo
	LogLevelWarning
	LogLevelError
)

func (l LogLevel) String() string {
	switch l {
	case LogLevelDebug:
		return "DEBUG"
	case LogLevelInfo:
		return "INFO"
	case LogLevelWarning:
		return "WARNING"
	case LogLevelError:
		return "ERROR"
	default:
		return "INFO"
	}
}

// Logger wraps logging functionality to match Python format exactly
type Logger struct {
	name   string
	writer io.Writer
	level  LogLevel
}

// GetLogger returns a logger instance for a module
func GetLogger(name string) *Logger {
	if name == "" {
		name = defaultLoggerName
	}
	if defaultLogger == nil {
		// Fallback logger if not initialized
		return &Logger{
			name:   name,
			writer: os.Stdout,
			level:  LogLevelInfo,
		}
	}
	return &Logger{
		name:   name,
		writer: defaultLogger.writer,
		level:  logLevel,
	}
}

// formatMessage formats a log message to match Python format exactly:
// "%(asctime)s - %(name)-25s - %(levelname)s - %(message)s"
// Date format: "%Y-%m-%d %H:%M:%S"
func (l *Logger) formatMessage(level LogLevel, format string, args ...interface{}) string {
	timestamp := time.Now().Format("2006-01-02 15:04:05")
	message := fmt.Sprintf(format, args...)
	levelStr := level.String()
	// Format: timestamp - logger_name (25 chars, left-aligned) - level - message
	return fmt.Sprintf("%s - %-25s - %s - %s\n", timestamp, l.name, levelStr, message)
}

// Info logs an INFO level message
func (l *Logger) Info(format string, args ...interface{}) {
	if l.level <= LogLevelInfo {
		msg := l.formatMessage(LogLevelInfo, format, args...)
		l.writer.Write([]byte(msg))
	}
}

// Error logs an ERROR level message
func (l *Logger) Error(format string, args ...interface{}) {
	if l.level <= LogLevelError {
		msg := l.formatMessage(LogLevelError, format, args...)
		l.writer.Write([]byte(msg))
	}
}

// Warning logs a WARNING level message
func (l *Logger) Warning(format string, args ...interface{}) {
	if l.level <= LogLevelWarning {
		msg := l.formatMessage(LogLevelWarning, format, args...)
		l.writer.Write([]byte(msg))
	}
}

// Debug logs a DEBUG level message
func (l *Logger) Debug(format string, args ...interface{}) {
	if l.level <= LogLevelDebug {
		msg := l.formatMessage(LogLevelDebug, format, args...)
		l.writer.Write([]byte(msg))
	}
}

// Printf is an alias for Info to maintain compatibility
func (l *Logger) Printf(format string, args ...interface{}) {
	l.Info(format, args...)
}

// Print is an alias for Info to maintain compatibility
func (l *Logger) Print(args ...interface{}) {
	l.Info("%s", fmt.Sprint(args...))
}

// InfoBanner logs a message with separator lines above and below (banner style)
func (l *Logger) InfoBanner(message string) {
	l.Info(logSeparator)
	l.Info(message)
	l.Info(logSeparator)
}

// InfoBannerf logs a formatted message with separator lines above and below (banner style)
func (l *Logger) InfoBannerf(format string, args ...interface{}) {
	l.Info(logSeparator)
	l.Info(format, args...)
	l.Info(logSeparator)
}

// InfoBannerStart logs the opening separator line
func (l *Logger) InfoBannerStart() {
	l.Info(logSeparator)
}

// InfoBannerEnd logs the closing separator line
func (l *Logger) InfoBannerEnd() {
	l.Info(logSeparator)
}

// LogTraceback logs a traceback-equivalent (stack trace) for errors
func (l *Logger) LogTraceback(err error) {
	l.Error("Error: %v", err)
	// Print stack trace
	buf := make([]byte, 4096)
	n := runtime.Stack(buf, false)
	stackTrace := string(buf[:n])
	lines := strings.Split(stackTrace, "\n")
	// Skip first line (goroutine info) and format like Python traceback
	for i, line := range lines {
		if i == 0 {
			continue
		}
		if strings.TrimSpace(line) == "" {
			continue
		}
		l.Error("  %s", line)
	}
}

// InitLogger initializes the default logger (called once at startup)
func InitLogger(level LogLevel, logFilePath string, alwaysLogToFile bool) (*Logger, error) {
	var logFileWriter io.Writer
	var err error

	if logFilePath == "" && alwaysLogToFile {
		// Create logs directory
		logsDir := "logs"
		if err := os.MkdirAll(logsDir, 0755); err != nil {
			return nil, fmt.Errorf("failed to create logs directory: %w", err)
		}
		// Create timestamped log file
		timestamp := time.Now().Format("20060102_150405")
		logFilePath = filepath.Join(logsDir, fmt.Sprintf("enva_%s.log", timestamp))
	}

	var writers []io.Writer
	writers = append(writers, os.Stdout)
	if logFilePath != "" {
		logFile, err = os.OpenFile(logFilePath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
		if err != nil {
			return nil, fmt.Errorf("failed to open log file: %w", err)
		}
		logFileWriter = logFile
		writers = append(writers, logFileWriter)
	}

	multiWriter := io.MultiWriter(writers...)
	logger := &Logger{
		name:   defaultLoggerName,
		writer: multiWriter,
		level:  level,
	}
	defaultLogger = logger
	logLevel = level

	return logger, nil
}

// GetDefaultLogger returns the default logger instance
func GetDefaultLogger() *Logger {
	if defaultLogger == nil {
		defaultLogger = &Logger{
			name:   defaultLoggerName,
			writer: os.Stdout,
			level:  LogLevelInfo,
		}
	}
	return defaultLogger
}

// CloseLogFile closes the log file if it's open
func CloseLogFile() {
	if logFile != nil {
		logFile.Close()
		logFile = nil
	}
}
