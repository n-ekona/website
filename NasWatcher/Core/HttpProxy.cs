using System.Net.Sockets;
using System.Text;

namespace NasWatcher.Core;

/// <summary>
/// モバイル端末 (ホットスポット経由) から NAS の Web 管理画面へ接続するための HTTP プロキシ。
/// NAS がリンクローカル (169.254.x.x) の場合にも、返信経路が無い NAS と通信できます。
/// リダイレクト (Location) や HTML 内のリンクを中継元のアドレスへ書き換えます。
/// </summary>
public sealed class HttpProxy : IDisposable
{
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private string? _targetHost;
    private int _targetPort;
    private string? _publicHost;
    private int _publicPort;
    private int _activeConnections;
    private string? _lastError;

    public sealed record Status(bool Running, int ListenPort, string? TargetHost, int TargetPort, int ActiveConnections, string? LastError);

    public Status GetStatus()
    {
        lock (_lock)
        {
            return new Status(_listener is not null, _publicPort, _targetHost, _targetPort, _activeConnections, _lastError);
        }
    }

    /// <summary>プロキシを開始します。</summary>
    public void Start(string listenAddress, int listenPort, string targetHost, int targetPort)
    {
        Stop();

        var address = System.Net.IPAddress.TryParse(listenAddress, out var ip) ? ip : System.Net.IPAddress.Any;
        var listener = new TcpListener(address, listenPort);
        listener.Start(16);

        var cts = new CancellationTokenSource();

        lock (_lock)
        {
            _listener = listener;
            _cts = cts;
            _targetHost = targetHost;
            _targetPort = targetPort;
            _publicHost = address.ToString();
            _publicPort = listenPort;
            _lastError = null;
        }

        _ = AcceptLoopAsync(listener, cts.Token);
        AppLog.Info($"HTTP プロキシを開始しました: {address}:{listenPort} → {targetHost}:{targetPort}");
    }

    /// <summary>プロキシを停止します。</summary>
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
            AppLog.Warn("HTTP プロキシの停止に失敗しました。", ex);
        }
        finally
        {
            cts?.Dispose();
        }

        if (listener is not null) AppLog.Info("HTTP プロキシを停止しました。");
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
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                lock (_lock) _lastError = ex.Message;
                await Task.Delay(300).ConfigureAwait(false);
                continue;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        string? targetHost;
        int targetPort;
        string publicHost;
        int publicPort;

        lock (_lock)
        {
            targetHost = _targetHost;
            targetPort = _targetPort;
            publicHost = _publicHost ?? "127.0.0.1";
            publicPort = _publicPort;
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

            await ProxyAsync(client.GetStream(), upstream.GetStream(), targetHost, targetPort, publicHost, publicPort, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
        }
        finally
        {
            lock (_lock) _activeConnections = Math.Max(0, _activeConnections - 1);
            try { client.Close(); } catch { }
            try { client.Dispose(); } catch { }
            try { upstream?.Close(); } catch { }
            try { upstream?.Dispose(); } catch { }
        }
    }

    private static async Task ProxyAsync(
        NetworkStream clientStream,
        NetworkStream upstreamStream,
        string targetHost,
        int targetPort,
        string publicHost,
        int publicPort,
        CancellationToken token)
    {
        // --- リクエストヘッダ ---
        var requestHead = await ReadHeadAsync(clientStream, token).ConfigureAwait(false);
        if (requestHead is null) return;

        var requestText = RewriteRequestHead(Encoding.UTF8.GetString(requestHead), targetHost, targetPort);
        var requestBytes = Encoding.UTF8.GetBytes(requestText);
        await upstreamStream.WriteAsync(requestBytes, token).ConfigureAwait(false);

        // --- レスポンスヘッダ ---
        var responseHead = await ReadHeadAsync(upstreamStream, token).ConfigureAwait(false);
        if (responseHead is null) return;

        var (headText, bodyStart) = SplitHead(responseHead);

        // NAS 自身が HTTPS へリダイレクトする場合、代理側で取得して内容を返す
        if (TryFollowRedirect(headText, targetHost, out var redirectUrl) && redirectUrl is not null)
        {
            await SendFetchedContentAsync(clientStream, redirectUrl, publicHost, publicPort, token).ConfigureAwait(false);
            return;
        }

        var rewritten = RewriteResponseHead(headText, targetHost, targetPort, publicHost, publicPort);
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes(rewritten), token).ConfigureAwait(false);

        if (bodyStart.Length > 0)
        {
            await clientStream.WriteAsync(bodyStart, token).ConfigureAwait(false);
        }

        // --- 本文 ---
        if (rewritten.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var body = new MemoryStream();
            await upstreamStream.CopyToAsync(body, 64 * 1024, token).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(body.ToArray());
            var baseTag = $"<base href=\"http://{publicHost}:{publicPort}/\">";

            if (text.Contains("<head", StringComparison.OrdinalIgnoreCase))
            {
                var index = text.IndexOf(">", StringComparison.Ordinal);
                if (index >= 0) text = text.Insert(index + 1, baseTag);
            }
            else
            {
                text = baseTag + text;
            }

            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
        }
        else
        {
            await upstreamStream.CopyToAsync(clientStream, 64 * 1024, token).ConfigureAwait(false);
        }
    }

    /// <summary>NAS 自身へのリダイレクト (例: http → https) であれば true を返します。</summary>
    private static bool TryFollowRedirect(string head, string targetHost, out string? url)
    {
        url = null;

        var lines = head.Split("\r\n");
        if (lines.Length == 0) return false;

        var statusParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out var status)) return false;
        if (status is not (301 or 302 or 303 or 307 or 308)) return false;

        foreach (var line in lines)
        {
            if (!line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line["Location:".Length..].Trim();
            if (value.StartsWith(targetHost, StringComparison.OrdinalIgnoreCase)
                || value.Contains($"://{targetHost}", StringComparison.OrdinalIgnoreCase))
            {
                url = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>リダイレクト先の内容を取得してクライアントへ返します。</summary>
    private static async Task SendFetchedContentAsync(
        NetworkStream clientStream,
        string url,
        string publicHost,
        int publicPort,
        CancellationToken token)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = true,
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync(url, token).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            var baseTag = $"<base href=\"http://{publicHost}:{publicPort}/\">";
            if (content.Contains("<head", StringComparison.OrdinalIgnoreCase))
            {
                var index = content.IndexOf(">", StringComparison.Ordinal);
                if (index >= 0) content = content.Insert(index + 1, baseTag);
            }
            else
            {
                content = baseTag + content;
            }

            var body = Encoding.UTF8.GetBytes(content);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";

            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(head), token).ConfigureAwait(false);
            await clientStream.WriteAsync(body, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Info($"リダイレクト先の取得に失敗しました ({url}): {ex.Message}");

            var message = Encoding.UTF8.GetBytes("<html><body>リダイレクト先の取得に失敗しました。</body></html>");
            var head = $"HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {message.Length}\r\nConnection: close\r\n\r\n";
            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(head), token).ConfigureAwait(false);
            await clientStream.WriteAsync(message, token).ConfigureAwait(false);
        }
    }

    /// <summary>ヘッダ部 (末尾の空行まで) を読みます。</summary>
    private static async Task<byte[]?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];

        while (buffer.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (read == 0) break;

            buffer.Add(one[0]);

            if (buffer.Count >= 4
                && buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n'
                && buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
            {
                return buffer.ToArray();
            }
        }

        return buffer.Count > 0 ? buffer.ToArray() : null;
    }

    private static (string Head, byte[] Body) SplitHead(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var index = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (index < 0) return (text, Array.Empty<byte>());

        var head = text[..(index + 4)];
        var body = data[(index + 4)..];
        return (head, body);
    }

    private static string RewriteRequestHead(string head, string targetHost, int targetPort)
    {
        var lines = head.Split("\r\n");
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"Host: {targetHost}:{targetPort}";
            }
            else if (lines[i].StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                // 本文を書き換えるため圧縮を無効化する
                lines[i] = "Accept-Encoding: identity";
            }
        }

        return string.Join("\r\n", lines);
    }

    private static string RewriteResponseHead(string head, string targetHost, int targetPort, string publicHost, int publicPort)
    {
        var lines = head.Split("\r\n");
        var targetForms = new[]
        {
            $"http://{targetHost}:{targetPort}",
            $"https://{targetHost}:{targetPort}",
            $"http://{targetHost}",
            $"https://{targetHost}",
        };

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("Location:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = lines[i]["Location:".Length..].Trim();
            foreach (var form in targetForms)
            {
                if (value.StartsWith(form, StringComparison.OrdinalIgnoreCase))
                {
                    value = $"http://{publicHost}:{publicPort}" + value[form.Length..];
                    lines[i] = $"Location: {value}";
                    break;
                }
            }
        }

        return string.Join("\r\n", lines);
    }

    public void Dispose() => Stop();
}
