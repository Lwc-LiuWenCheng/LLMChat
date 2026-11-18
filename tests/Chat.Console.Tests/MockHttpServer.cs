using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chat.Console.Tests;

internal sealed record CapturedRequest(string Method, string Path, string Body);

/// <summary>
/// 极简 HTTP/1.1 mock：接受一次连接、记录请求体，并按 DeepSeek Anthropic SSE 格式回两帧，
/// 让 CLI 能真实走完“HTTP 请求 → 解析 SSE → Done”的路径。
/// </summary>
internal sealed class MockHttpServer : IAsyncDisposable
{
    private const string SsePayload =
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<CapturedRequest> _request =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;

    public MockHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public Task<CapturedRequest> Request => _request.Task.WaitAsync(TimeSpan.FromSeconds(30));

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
                _request.TrySetResult(request);

                var payload = Encoding.UTF8.GetBytes(SsePayload);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _request.TrySetException(ex);
            }
        }
    }

    private static async Task<CapturedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("客户端在请求头结束前关闭了连接。");
            buffer.Write(chunk, 0, read);
            headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        var data = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var method = requestLine[0];
        var path = requestLine.Length > 1 ? requestLine[1] : "/";

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(colon + 1)..].Trim());
        }

        var bodyStart = headerEnd + 4;
        var bodyBytes = new List<byte>(Math.Max(0, data.Length - bodyStart));
        bodyBytes.AddRange(data[bodyStart..]);
        while (bodyBytes.Count < contentLength)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            bodyBytes.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var body = Encoding.UTF8.GetString(bodyBytes.ToArray(), 0, Math.Min(contentLength, bodyBytes.Count));
        return new CapturedRequest(method, path, body);
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
                return i;
        }

        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 监听器停止时接受循环以异常收尾是预期行为。
        }

        _cts.Dispose();
    }
}
