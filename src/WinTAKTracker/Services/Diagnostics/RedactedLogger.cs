using System.Text;
using System.Text.RegularExpressions;

namespace WinTAKTracker.Services.Diagnostics;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
}

public interface IRedactedLogger
{
    void SetMinLevel(LogLevel level);
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);
    void Debug(string category, string message);
    string LogsDirectory { get; }
    void ClearOldLogs(TimeSpan olderThan);
}

/// <summary>Rotating file logger that redacts tokens, passwords, and enroll URLs.</summary>
public sealed class RedactedLogger : IRedactedLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _logsDir;
    private LogLevel _min = LogLevel.Information;
    private StreamWriter? _writer;
    private string? _currentPath;
    private DateTime _currentDay = DateTime.MinValue;

    private static readonly Regex SecretPatterns = new(
        @"(?i)(password|passwd|pwd|token|secret|authorization)=([^\s&""']+)|" +
        @"(opentaktracker://[^\s]+)|(tak://[^\s]+)|" +
        @"(-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----)",
        RegexOptions.Compiled);

    public RedactedLogger(string logsDirectory)
    {
        _logsDir = logsDirectory;
        Directory.CreateDirectory(_logsDir);
    }

    public string LogsDirectory => _logsDir;

    public void SetMinLevel(LogLevel level) => _min = level;

    public void Info(string category, string message) => Write(LogLevel.Information, category, message);
    public void Warn(string category, string message) => Write(LogLevel.Warning, category, message);
    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public void Error(string category, string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write(LogLevel.Error, category, full);
    }

    public void ClearOldLogs(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var file in Directory.EnumerateFiles(_logsDir, "wintaktracker-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { /* ignore */ }
        }
    }

    private void Write(LogLevel level, string category, string message)
    {
        if (level < _min) return;
        var redacted = Redact(message);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {category}: {redacted}";
        lock (_gate)
        {
            EnsureWriter();
            _writer!.WriteLine(line);
            _writer.Flush();
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.Now.Date;
        if (_writer is not null && _currentDay == today) return;
        _writer?.Dispose();
        _currentDay = today;
        _currentPath = Path.Combine(_logsDir, $"wintaktracker-{today:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = SecretPatterns.Replace(input, m =>
        {
            if (m.Groups[1].Success)
                return $"{m.Groups[1].Value}=***";
            if (m.Groups[3].Success || m.Groups[4].Success)
                return "[REDACTED_ENROLL_URL]";
            return "[REDACTED_KEY_MATERIAL]";
        });

        // Partially mask host-like tokens after host=
        result = Regex.Replace(result, @"(?i)\b(host=)([A-Za-z0-9.\-]+)", m =>
        {
            var host = m.Groups[2].Value;
            if (host.Length <= 4) return m.Groups[1].Value + "***";
            return m.Groups[1].Value + host[..2] + "***" + host[^2..];
        });

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
