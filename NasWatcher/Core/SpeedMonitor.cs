using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NasWatcher.Core;

/// <summary>1 .sample の通信速度。</summary>
public sealed record SpeedSample(
    string AdapterId,
    string AdapterName,
    double RxBytesPerSecond,
    double TxBytesPerSecond,
    long RxTotalBytes,
    long TxTotalBytes);

/// <summary>各アダプターの通信速度を一定間隔で計測します。</summary>
public sealed class SpeedMonitor : IDisposable
{
    private readonly Dictionary<string, (long Rx, long Tx, long Timestamp)> _previous = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    /// <summary>計測結果が更新されたときに発火します。</summary>
    public event Action<IReadOnlyList<SpeedSample>>? Updated;

    public SpeedMonitor(TimeSpan interval)
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, interval);
    }

    private void Tick()
    {
        if (_disposed) return;

        try
        {
            var samples = Collect();
            Updated?.Invoke(samples);
        }
        catch (Exception ex)
        {
            AppLog.Warn("通信速度の計測に失敗しました。", ex);
        }
    }

    /// <summary>現在の速度を 1 回だけ計測します。</summary>
    public IReadOnlyList<SpeedSample> Collect()
    {
        var samples = new List<SpeedSample>();
        var now = Stopwatch.GetTimestamp();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            long rx = 0, tx = 0;
            try
            {
                var stats = ni.GetIPv4Statistics();
                rx = stats.BytesReceived;
                tx = stats.BytesSent;
            }
            catch
            {
                continue;
            }

            var id = ni.Id;
            double rxPerSecond = 0;
            double txPerSecond = 0;

            if (_previous.TryGetValue(id, out var previous))
            {
                var elapsed = (now - previous.Timestamp) / (double)Stopwatch.Frequency;
                if (elapsed > 0.05)
                {
                    rxPerSecond = Math.Max(0, (rx - previous.Rx) / elapsed);
                    txPerSecond = Math.Max(0, (tx - previous.Tx) / elapsed);
                }
            }

            _previous[id] = (rx, tx, now);
            samples.Add(new SpeedSample(id, ni.Name ?? ni.Description ?? id, rxPerSecond, txPerSecond, rx, tx));
        }

        return samples;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
