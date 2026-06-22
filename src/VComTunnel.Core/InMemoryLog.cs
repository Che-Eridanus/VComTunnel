using System.Collections.Concurrent;
using System.Text;

namespace VComTunnel.Core;

public sealed class InMemoryLog : IDisposable
{
    private const int MaxEntries = 1000;
    private const long DefaultMaxFileBytes = 2 * 1024 * 1024;
    private const int DefaultMaxArchiveFiles = 5;
    private const string ActiveLogFileName = "service.log";
    private static readonly object FileLock = new();

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly ConcurrentQueue<LogEntry> _pendingFileEntries = new();
    private readonly string _logsDirectory;
    private readonly long _maxFileBytes;
    private readonly int _maxArchiveFiles;
    private int _entryCount;
    private int _fileFlushScheduled;
    private int _disposed;

    public InMemoryLog()
        : this(null, DefaultMaxFileBytes, DefaultMaxArchiveFiles)
    {
    }

    public InMemoryLog(string? logsDirectory, long maxFileBytes = DefaultMaxFileBytes, int maxArchiveFiles = DefaultMaxArchiveFiles)
    {
        if (maxFileBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "Log file limit must be at least 1 KiB.");
        }

        if (maxArchiveFiles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxArchiveFiles), "Archive count cannot be negative.");
        }

        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory) ? AppPaths.LogsDirectory : logsDirectory;
        _maxFileBytes = maxFileBytes;
        _maxArchiveFiles = maxArchiveFiles;
    }

    public void Info(string source, string message) => Add("info", source, message);
    public void Warn(string source, string message) => Add("warn", source, message);
    public void Error(string source, string message) => Add("error", source, message);

    public IReadOnlyList<LogEntry> Snapshot(int max = 500)
    {
        return _entries.Reverse().Take(max).Reverse().ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _entryCount);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FlushPendingFileEntries();
    }

    private void Add(string level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, source, message);
        _entries.Enqueue(entry);
        Interlocked.Increment(ref _entryCount);
        _pendingFileEntries.Enqueue(entry);
        ScheduleFileFlush();
        while (Volatile.Read(ref _entryCount) > MaxEntries && _entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _entryCount);
        }
    }

    private void ScheduleFileFlush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _fileFlushScheduled, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => FlushFileQueue());
        }
    }

    private void FlushFileQueue()
    {
        do
        {
            FlushPendingFileEntries();
            Interlocked.Exchange(ref _fileFlushScheduled, 0);
        }
        while (!_pendingFileEntries.IsEmpty && Interlocked.Exchange(ref _fileFlushScheduled, 1) == 0);
    }

    private void FlushPendingFileEntries()
    {
        try
        {
            lock (FileLock)
            {
                var lines = new List<string>();
                while (_pendingFileEntries.TryDequeue(out var entry))
                {
                    lines.Add(FormatLogEntry(entry));
                }

                if (lines.Count == 0)
                {
                    return;
                }

                Directory.CreateDirectory(_logsDirectory);
                var activePath = Path.Combine(_logsDirectory, ActiveLogFileName);
                foreach (var line in lines)
                {
                    AppendLogLine(activePath, line);
                }
            }
        }
        catch
        {
        }
    }

    private void AppendLogLine(string activePath, string line)
    {
        var cappedLine = CapLineToFileLimit(line);
        RotateIfNeeded(activePath, Encoding.UTF8.GetByteCount(cappedLine));
        File.AppendAllText(activePath, cappedLine, Encoding.UTF8);
    }

    private string CapLineToFileLimit(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= _maxFileBytes)
        {
            return line;
        }

        const string suffix = " ... [truncated]\n";
        var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
        var budget = Math.Max(1, _maxFileBytes - suffixBytes);
        var low = 0;
        var high = line.Length;
        var best = 0;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (Encoding.UTF8.GetByteCount(line.AsSpan(0, mid)) <= budget)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return line[..best] + suffix;
    }

    private void RotateIfNeeded(string activePath, int incomingBytes)
    {
        if (!File.Exists(activePath))
        {
            return;
        }

        var length = new FileInfo(activePath).Length;
        if (length == 0 || length + incomingBytes <= _maxFileBytes)
        {
            return;
        }

        if (_maxArchiveFiles == 0)
        {
            File.Delete(activePath);
            return;
        }

        for (var index = _maxArchiveFiles; index >= 1; index--)
        {
            var source = index == 1 ? activePath : ArchivePath(activePath, index - 1);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = ArchivePath(activePath, index);
            MoveLogFileWithLimit(source, destination);
        }
    }

    private void MoveLogFileWithLimit(string source, string destination)
    {
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        if (new FileInfo(source).Length <= _maxFileBytes)
        {
            File.Move(source, destination);
            return;
        }

        CopyTailWithLimit(source, destination);
        File.Delete(source);
    }

    private void CopyTailWithLimit(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = File.Create(destination);
        var start = Math.Max(0, input.Length - _maxFileBytes);
        input.Seek(start, SeekOrigin.Begin);
        input.CopyTo(output);
    }

    private static string ArchivePath(string activePath, int index)
    {
        var directory = Path.GetDirectoryName(activePath) ?? ".";
        return Path.Combine(directory, $"service.{index}.log");
    }

    private static string FormatLogEntry(LogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Timestamp.ToString("O"));
        builder.Append(' ');
        builder.Append(entry.Level.PadRight(5));
        builder.Append(' ');
        builder.Append(entry.Source);
        builder.Append(": ");
        builder.Append(entry.Message);
        builder.AppendLine();
        return builder.ToString();
    }
}
