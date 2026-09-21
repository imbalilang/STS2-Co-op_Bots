using System.Threading.Channels;

namespace CoopBots.Kernel.Vendor.Replay;

internal sealed record EventLogSnapshot(byte[] JsonLines, long EventCount, string? Error,
    long PeakPendingBytes, long WrittenBytes);

// One background writer owns the temporary file. Append never waits for disk I/O;
// snapshots are FIFO barriers and preserve their prefix even while combat continues.
internal sealed class AppendOnlyEventLog<T> : IDisposable
{
    private sealed record Message(T? Item, int Charge, TaskCompletionSource<EventLogSnapshot>? Snapshot);
    private readonly Channel<Message> _messages = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Func<T, byte[]> _serialize;
    private readonly long _maximumPendingBytes;
    private readonly long _maximumFileBytes;
    private readonly string? _outputPath;
    private long _pendingBytes;
    private long _peakPendingBytes;
    private int _pendingSnapshots;
    private string? _error;
    public string? Error => Volatile.Read(ref _error);
    public Task Completion { get; }

    public AppendOnlyEventLog(Func<T, byte[]> serialize, long maximumPendingBytes = 8L * 1024 * 1024,
        long maximumFileBytes = 32L * 1024 * 1024, string? outputPath = null)
    {
        _serialize = serialize;
        _maximumPendingBytes = maximumPendingBytes;
        _maximumFileBytes = maximumFileBytes;
        _outputPath = outputPath;
        Completion = Task.Run(WriteAsync);
    }

    public bool TryAppend(T item, int estimatedBytes)
    {
        if (Error != null) return false;
        int charge = checked(estimatedBytes + 256);
        long pending = Interlocked.Add(ref _pendingBytes, charge);
        if (pending > _maximumPendingBytes)
        {
            Interlocked.Add(ref _pendingBytes, -charge);
            SetError("event_pending_memory_limit");
            return false;
        }
        long peak;
        do { peak = Volatile.Read(ref _peakPendingBytes); }
        while (pending > peak && Interlocked.CompareExchange(ref _peakPendingBytes, pending, peak) != peak);
        if (_messages.Writer.TryWrite(new Message(item, charge, null))) return true;
        Interlocked.Add(ref _pendingBytes, -charge);
        throw new ObjectDisposedException(nameof(AppendOnlyEventLog<T>));
    }

    public Task<EventLogSnapshot> CaptureAsync()
    {
        if (Interlocked.Increment(ref _pendingSnapshots) > 2)
        {
            Interlocked.Decrement(ref _pendingSnapshots);
            throw new InvalidOperationException("event_export_backlog_limit");
        }
        TaskCompletionSource<EventLogSnapshot> snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_messages.Writer.TryWrite(new Message(default, 0, snapshot)))
        {
            Interlocked.Decrement(ref _pendingSnapshots);
            throw new ObjectDisposedException(nameof(AppendOnlyEventLog<T>));
        }
        return snapshot.Task;
    }

    private async Task WriteAsync()
    {
        FileStream? file = null;
        long count = 0;
        try
        {
            try
            {
                if (_outputPath != null) Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
                file = new FileStream(_outputPath ?? Path.Combine(Path.GetTempPath(), "CombatSolver-events-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536,
                    _outputPath == null ? FileOptions.DeleteOnClose : FileOptions.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { SetError("event_file_open_failed:" + error.Message); }
            await foreach (Message message in _messages.Reader.ReadAllAsync())
            {
                if (message.Snapshot is { } snapshot)
                {
                    try
                    {
                        byte[] bytes = file == null ? [] : new byte[checked((int)file.Length)];
                        if (file != null)
                        {
                            file.Position = 0;
                            file.ReadExactly(bytes);
                        }
                        snapshot.SetResult(new EventLogSnapshot(bytes, count, Error, Volatile.Read(ref _peakPendingBytes), bytes.LongLength));
                    }
                    catch (IOException error)
                    {
                        SetError("event_snapshot_failed:" + error.Message);
                        snapshot.SetResult(new EventLogSnapshot([], 0, Error, Volatile.Read(ref _peakPendingBytes), 0));
                    }
                    finally { Interlocked.Decrement(ref _pendingSnapshots); }
                    continue;
                }
                try
                {
                    if (file == null || Error != null) continue;
                    byte[] bytes = _serialize(message.Item!);
                    if (file.Length + bytes.Length + 1 > _maximumFileBytes) { SetError("event_file_size_limit"); continue; }
                    file.Write(bytes);
                    file.WriteByte((byte)'\n');
                    count++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
                { SetError("event_write_failed:" + error.Message); }
                finally { Interlocked.Add(ref _pendingBytes, -message.Charge); }
            }
        }
        finally { file?.Dispose(); }
    }
    private void SetError(string value) => Interlocked.CompareExchange(ref _error, value, null);
    public void Dispose() => _messages.Writer.TryComplete();
}
