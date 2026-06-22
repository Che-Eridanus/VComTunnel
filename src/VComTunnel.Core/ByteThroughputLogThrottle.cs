using System.Diagnostics;

namespace VComTunnel.Core;

internal sealed class ByteThroughputLogThrottle
{
    private readonly long _intervalTicks;
    private long _nextFlushTicks;
    private long _bytes;
    private int _chunks;

    public ByteThroughputLogThrottle(TimeSpan interval)
    {
        _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
    }

    public void Record(int bytes, Action<long, int> flush)
    {
        _bytes += bytes;
        _chunks++;

        var now = Stopwatch.GetTimestamp();
        if (_nextFlushTicks != 0 && now < _nextFlushTicks)
        {
            return;
        }

        flush(_bytes, _chunks);
        _bytes = 0;
        _chunks = 0;
        _nextFlushTicks = now + _intervalTicks;
    }
}