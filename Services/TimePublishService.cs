using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NtpServer.Models;

namespace NtpServer.Services;

/// <summary>
/// 局域网时间发布服务：
/// 1) HTTP 服务端：监听 <see cref="PublishSettings.Port"/>，GET /api/time 返回设备名 + 当前时间。
/// 2) 组播注册：每 5 秒向 239.255.42.42:47492 发送 JSON 描述自身，便于同步端发现。
/// </summary>
public class TimePublishService : IDisposable
{
    private const string MulticastAddress = "239.255.42.42";
    private const int MulticastPort = 47492;
    private const int MulticastIntervalMs = 5_000;
    private const int MulticastTtl = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    private readonly ILogger<TimePublishService> _logger;
    private readonly IExactTimeServiceProvider _timeProvider;
    private readonly Func<PublishSettings> _settingsAccessor;

    private TcpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private Task? _multicastLoop;
    private bool _disposed;

    public TimePublishService(
        ILogger<TimePublishService> logger,
        IExactTimeServiceProvider timeProvider,
        Func<PublishSettings> settingsAccessor)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _settingsAccessor = settingsAccessor;
    }

    public bool IsRunning
    {
        get
        {
            try { return _httpListener?.Server.IsBound == true; }
            catch { return false; }
        }
    }

    public long RequestCount { get; private set; }

    public string? LastError { get; private set; }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TimePublishService));
        if (IsRunning) return;
        var settings = _settingsAccessor();
        if (string.IsNullOrWhiteSpace(settings.DeviceName))
        {
            LastError = "设备名为空，无法启动发布服务";
            _logger.LogError("[NtpServer.Publish] {Error}", LastError);
            return;
        }

        try
        {
            _httpListener = new TcpListener(IPAddress.Any, settings.Port);
            _httpListener.Start();
            LastError = null;
            _logger.LogInformation("[NtpServer.Publish] 时间发布 HTTP 服务已启动，端口: {Port}", settings.Port);
        }
        catch (Exception ex)
        {
            LastError = $"监听端口 {settings.Port} 失败: {ex.Message}";
            _logger.LogError(ex, "[NtpServer.Publish] 启动失败: {Message}", ex.Message);
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _multicastLoop = Task.Run(() => MulticastLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _httpListener?.Stop(); } catch { /* 忽略 */ }
        _httpListener = null;
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        _cts?.Dispose();
        _cts = null;
        _logger?.LogInformation("[NtpServer.Publish] 时间发布服务已停止");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _httpListener;
        if (listener == null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (ObjectDisposedException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NtpServer.Publish] 接受连接时发生错误: {Message}", ex.Message);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            // 读取请求行（最多读取 8 KB 防止恶意大请求）
            var requestLine = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine))
            {
                client.Close();
                return;
            }
            // 读取并丢弃所有 header
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token).ConfigureAwait(false))) { }

            var settings = _settingsAccessor();
            var path = requestLine.Split(' ')[1];
            byte[] responseBytes;
            string status;
            if (path.Equals("/api/time", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                var localNow = _timeProvider.GetCurrentLocalDateTime();
                var payload = new TimeResponse
                {
                    DeviceName = settings.DeviceName,
                    UtcTicks = localNow.ToUniversalTime().Ticks,
                    LocalIso = localNow.ToString("O"),
                    Stratum = "1",
                    ServerTime = DateTime.UtcNow
                };
                var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                responseBytes = BuildHttpResponse("200 OK", body, "application/json");
                status = "200";
                Interlocked.Increment(ref _requestCount);
            }
            else if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                var body = Encoding.UTF8.GetBytes("OK");
                responseBytes = BuildHttpResponse("200 OK", body, "text/plain");
                status = "200";
            }
            else
            {
                var body = Encoding.UTF8.GetBytes("Not Found");
                responseBytes = BuildHttpResponse("404 Not Found", body, "text/plain");
                status = "404";
            }
            await stream.WriteAsync(responseBytes, token).ConfigureAwait(false);
            _logger.LogDebug("[NtpServer.Publish] {Status} {Path} from {Remote}", status, path, client.Client.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NtpServer.Publish] 处理 HTTP 请求时发生异常: {Message}", ex.Message);
        }
        finally
        {
            try { client.Close(); } catch { /* 忽略 */ }
        }
    }

    private static byte[] BuildHttpResponse(string status, byte[] body, string contentType)
    {
        var headers =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        var result = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, result, headerBytes.Length, body.Length);
        return result;
    }

    private async Task MulticastLoopAsync(CancellationToken token)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, MulticastTtl);
            var endpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = _settingsAccessor();
                    var ips = LocalIpsHelper.GetLocalIpv4Addresses();
                    foreach (var ip in ips)
                    {
                        var device = new DeviceInfo
                        {
                            DeviceName = settings.DeviceName,
                            Ip = ip,
                            Port = settings.Port,
                            LastSeen = DateTime.UtcNow
                        };
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(device, JsonOptions);
                        await udp.SendAsync(bytes, bytes.Length, endpoint).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[NtpServer.Publish] 组播注册失败: {Message}", ex.Message);
                }
                try { await Task.Delay(MulticastIntervalMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NtpServer.Publish] 组播循环异常退出: {Message}", ex.Message);
        }
        finally
        {
            udp?.Close();
        }
    }

    private class TimeResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("utcTicks")]
        public long UtcTicks { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("localIso")]
        public string LocalIso { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("stratum")]
        public string Stratum { get; set; } = "1";

        [System.Text.Json.Serialization.JsonPropertyName("serverTime")]
        public DateTime ServerTime { get; set; }
    }
}

/// <summary>
/// 抽象出 IExactTimeService 的访问，便于 PublishService 解耦。
/// </summary>
public interface IExactTimeServiceProvider
{
    DateTime GetCurrentLocalDateTime();
}
