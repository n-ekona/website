using System.Net;
using System.Net.Sockets;

namespace NasWatcher.Core;

/// <summary>
/// モバイル端末 (ホットスポット経由) から NAS へ接続するための TCP リレー。
/// NAS がリンクローカル (169.254.x.x) のように戻経路を持たない場合でも、
/// PC が中継することで接続できます。
/// </summary>
public sealed class TcpRelay : IDisposable
{
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activeConnections;

    /// <summary>中継.stats.</summary>
    public sealed record Status(bool Running, int ListenPort, string? TargetHost, int TargetPort, int ActiveConnections, string? LastError);

    public Status GetStatus()
    {
        lock (_lock)
        {
            return new Status(
                _listener is not null,
                _listenPort,
                _targetHost,
                _targetPort,
                _activeConnections,
                _lastError);
        }
    }

    private int _listenPort;
    private string? _targetHost;
    private int _targetPort;
    private string? _lastError;

    /// <summary>リレーを開始します。既に開始済みの場合は再設定します。</summary>
    public void Start(string listenAddress, int listenPort, string targetHost, int targetPort)
    {
        Stop();

        var address = IPAddress.TryParse(listenAddress, out var ip) ? ip : IPAddress.Any;

        var listener = new TcpListener(address, listenPort);
        listener.Start(16);

        var cts = new CancellationTokenSource();

        lock (_lock)
        {
            _listener = listener;
            _cts = cts;
            _listenPort = listenPort;
            _targetHost = targetHost;
            _targetPort = targetPort;
            _lastError = null;
        }

        _ = AcceptLoopAsync(listener, cts.Token);
        AppLog.Info($"TCP リレーを開始しました: {address}:{listenPort} → {targetHost}:{targetPort}");
    }

    /// <summary>リレーを停止します。</summary>
    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;

        lock (_lock)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            _activeConnections = 0;
        }

        try
        {
            cts?.Cancel();
            listener?.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn("TCP リレーの停止に失敗しました。", ex);
        }
        finally
        {
            cts?.Dispose();
        }

        if (listener is not null) AppLog.Info("TCP リレーを停止しました。");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_lock) _lastError = ex.Message;
                AppLog.Warn("TCP リレーの接続受付に失敗しました。", ex);
                await Task.Delay(500).ConfigureAwait(false);
                continue;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        string? targetHost;
        int targetPort;

        lock (_lock)
        {
            targetHost = _targetHost;
            targetPort = _targetPort;
            _activeConnections++;
        }

        TcpClient? upstream = null;

        try
        {
            if (string.IsNullOrEmpty(targetHost)) return;

            upstream = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(8000);
            await upstream.ConnectAsync(targetHost, targetPort, connectCts.Token).ConfigureAwait(false);

            var clientStream = client.GetStream();
            var upstreamStream = upstream.GetStream();

            var toUpstream = clientStream.CopyToAsync(upstreamStream, 64 * 1024, token);
            var toClient = upstreamStream.CopyToAsync(clientStream, 64 * 1024, token);

            await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
            AppLog.Info($"リレー接続に失敗しました ({targetHost}:{targetPort}): {ex.Message}");
        }
        finally
        {
            lock (_lock) _activeConnections = Math.Max(0, _activeConnections - 1);

            try { client.Close(); } catch { /* 破棄済み */ }
            try { upstream?.Close(); } catch { /* 破棄済み */ }
            try { upstream?.Dispose(); } catch { /* 破棄済み */ }
            try { client.Dispose(); } catch { /* 破棄済み */ }
        }
    }

    public void Dispose() => Stop();
}
