using System.Text;
using System.Threading.Channels;

public sealed class ChannelLogSink : ILogSink, IAsyncDisposable
{
    private readonly Channel<LogEvent> _channel;
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(250);
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private long _dropped;
    private Task? _worker;
    private CancellationTokenSource? _workerCts;

    public ChannelLogSink(int capacity = 5000)
    {
        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Enqueue(LogEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
            Interlocked.Increment(ref _dropped);
    }

    public void Start(CancellationToken externalCt = default)
    {
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _worker = Task.Run(() => RunAsync(_workerCts.Token), CancellationToken.None);
    }

    public async Task DrainAndStopAsync()
    {
        _channel.Writer.TryComplete();
        if (_worker != null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch { }
        }
        await FlushAndCloseAllAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAndStopAsync().ConfigureAwait(false);
        _workerCts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastFlush = DateTime.UtcNow;
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var evt))
                {
                    try
                    {
                        var writer = GetOrCreateWriter(evt.FilePath);
                        await writer.WriteAsync(evt.Message).ConfigureAwait(false);
                    }
                    catch
                    {
                        // IO failure on a single entry is non-fatal.
                    }
                }

                var now = DateTime.UtcNow;
                if (now - lastFlush >= _flushInterval)
                {
                    await FlushAllAsync().ConfigureAwait(false);
                    lastFlush = now;
                }
            }
        }
        catch (OperationCanceledException) { }

        // Drain any remaining events after channel completion or cancellation.
        while (_channel.Reader.TryRead(out var remaining))
        {
            try
            {
                var writer = GetOrCreateWriter(remaining.FilePath);
                await writer.WriteAsync(remaining.Message).ConfigureAwait(false);
            }
            catch { }
        }

        await FlushAllAsync().ConfigureAwait(false);
    }

    private StreamWriter GetOrCreateWriter(string filePath)
    {
        if (!_writers.TryGetValue(filePath, out var writer))
        {
            writer = new StreamWriter(filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            _writers[filePath] = writer;
        }
        return writer;
    }

    private async Task FlushAllAsync()
    {
        foreach (var writer in _writers.Values)
        {
            try { await writer.FlushAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task FlushAndCloseAllAsync()
    {
        foreach (var writer in _writers.Values)
        {
            try
            {
                await writer.FlushAsync().ConfigureAwait(false);
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch { }
        }
        _writers.Clear();
    }
}
