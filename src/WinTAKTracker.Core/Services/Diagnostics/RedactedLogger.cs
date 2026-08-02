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
    void SetMaxTotalSizeMb(int megabytes);
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);
    void Debug(string category, string message);
    string LogsDirectory { get; }
    void ClearOldLogs(TimeSpan olderThan);
    void EnforceSizeLimit();
}

/// <summary>Rotating file logger that redacts tokens, passwords, and enroll URLs.</summary>
public sealed class RedactedLogger : IRedactedLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _logsDir;
    private LogLevel _min = LogLevel.Error;
    private long _maxTotalBytes = 30L * 1024 * 1024;
    private StreamWriter? _writer;
    private string? _currentPath;
    private DateTime _currentDay = DateTime.MinValue;
    private int _writesSinceTrim;

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

    public void SetMaxTotalSizeMb(int megabytes)
    {
        var mb = Math.Clamp(megabytes, 1, 1024);
        lock (_gate)
            _maxTotalBytes = mb * 1024L * 1024L;
    }

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

    public void EnforceSizeLimit()
    {
        lock (_gate)
            TrimToLimitUnlocked();
    }

    private void Write(LogLevel level, string category, string message)
    {
        // Video ops always keep Information+ so stream start/stop/FFmpeg failures are diagnosable
        // even when Diagnostics.LogLevel is Error.
        var videoOps = string.Equals(category, "Video", StringComparison.OrdinalIgnoreCase);
        if (videoOps)
        {
            if (level < LogLevel.Information) return;
        }
        else if (level < _min)
        {
            return;
        }

        var redacted = Redact(message);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {category}: {redacted}";
        lock (_gate)
        {
            EnsureWriter();
            _writer!.WriteLine(line);
            _writer.Flush();
            _writesSinceTrim++;
            if (_writesSinceTrim >= 25)
            {
                _writesSinceTrim = 0;
                TrimToLimitUnlocked();
            }
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

    private void TrimToLimitUnlocked()
    {
        try
        {
            var files = Directory.EnumerateFiles(_logsDir, "wintaktracker-*.log")
                .Select(p =>
                {
                    var info = new FileInfo(p);
                    return (Path: p, info.Length, info.LastWriteTimeUtc);
                })
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= _maxTotalBytes) return;

            // Prefer deleting oldest full files first (keep today's active log when possible).
            foreach (var file in files)
            {
                if (total <= _maxTotalBytes) break;
                if (string.Equals(file.Path, _currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(file.Path);
                    total -= file.Length;
                }
                catch { /* ignore */ }
            }

            // If still over (single huge active file), truncate by rewriting the tail.
            if (total > _maxTotalBytes && _currentPath is not null && File.Exists(_currentPath))
            {
                try
                {
                    _writer?.Dispose();
                    _writer = null;
                    var keep = Math.Max(_maxTotalBytes / 2, 256 * 1024);
                    var bytes = File.ReadAllBytes(_currentPath);
                    if (bytes.Length > keep)
                    {
                        var start = bytes.Length - (int)keep;
                        // Align to next newline so we don't keep a partial line.
                        while (start < bytes.Length && bytes[start] != (byte)'\n')
                            start++;
                        if (start < bytes.Length)
                            start++;
                        File.WriteAllBytes(_currentPath, bytes[start..]);
                    }

                    _currentDay = DateTime.MinValue;
                    EnsureWriter();
                }
                catch
                {
                    _currentDay = DateTime.MinValue;
                    EnsureWriter();
                }
            }
        }
        catch { /* ignore */ }
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
