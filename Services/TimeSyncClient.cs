using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NtpServer.Models;

namespace NtpServer.Services;

/// <summary>
/// 时间同步客户端：订阅组播发现其他发布端，并按 IP/设备名请求时间。
/// </summary>
public class TimeSyncClient : IDisposable
{
    private const string MulticastAddress = "239.255.42.42";
    private const int MulticastPort = 47492;
    private const int PruneAfterSeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<TimeSyncClient> _logger;
    private readonly IExactTimeServiceProvider _timeProvider;
    private readonly Func<SyncSettings> _settingsAccessor;

    private UdpClient? _discoveryClient;
    private CancellationTokenSource? _cts;
    private Task? _discoveryLoop;
    private bool _disposed;

    public TimeSyncClient(
        ILogger<TimeSyncClient> logger,
        IExactTimeServiceProvider timeProvider,
        Func<SyncSettings> settingsAccessor)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _settingsAccessor = settingsAccessor;
    }

    /// <summary>当前已发现的设备（deviceName → DeviceInfo），按时间降序展示。</summary>
    public ConcurrentDictionary<string, DeviceInfo> Discovered { get; } = new();

    public bool IsDiscoveryRunning => _discoveryLoop is { IsCompleted: false };

    public void StartDiscovery()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TimeSyncClient));
        if (IsDiscoveryRunning) return;
        try
        {
            _discoveryClient = new UdpClient(AddressFamily.InterNetwork);
            _discoveryClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discoveryClient.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            _discoveryClient.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
            _cts = new CancellationTokenSource();
            _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(_cts.Token));
            _logger.LogInformation("[NtpServer.Sync] 已加入组播 239.255.42.42:{Port}", MulticastPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NtpServer.Sync] 启动设备发现失败: {Message}", ex.Message);
        }
    }

    public void StopDiscovery()
    {
        try { _discoveryClient?.DropMulticastGroup(IPAddress.Parse(MulticastAddress)); } catch { /* 忽略 */ }
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        try { _discoveryClient?.Close(); } catch { /* 忽略 */ }
        _discoveryClient = null;
        _cts?.Dispose();
        _cts = null;
        _logger.LogInformation("[NtpServer.Sync] 已停止设备发现");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopDiscovery();
        GC.SuppressFinalize(this);
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        var client = _discoveryClient;
        if (client == null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await client.ReceiveAsync(token).ConfigureAwait(false);
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    var device = JsonSerializer.Deserialize<DeviceInfo>(json, JsonOptions);
                    if (device != null && !string.IsNullOrEmpty(device.DeviceName))
                    {
                        device.LastSeen = DateTime.UtcNow;
                        Discovered[device.DeviceName] = device;
                        _logger.LogDebug("[NtpServer.Sync] 发现设备 {Name} {Ip}:{Port}",
                            device.DeviceName, device.Ip, device.Port);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[NtpServer.Sync] 解析组播包失败: {Message}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NtpServer.Sync] 发现循环异常退出: {Message}", ex.Message);
        }
    }

    /// <summary>清理过期的设备条目。</summary>
    public void PruneStale()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in Discovered)
        {
            if ((now - kv.Value.LastSeen).TotalSeconds > PruneAfterSeconds)
            {
                Discovered.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>通过 HTTP 向指定设备请求时间。</summary>
    public async Task<TimeResponse?> RequestAsync(string ip, int port, int timeoutMs = 3000, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(IPAddress.Parse(ip), port, cts.Token).ConfigureAwait(false);
            using var stream = tcp.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET /api/time HTTP/1.1\r\nHost: {ip}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cts.Token).ConfigureAwait(false);
            var raw = Encoding.UTF8.GetString(ms.ToArray());

            // 简单 HTTP 解析：取 \r\n\r\n 后为 body
            var sep = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (sep < 0) return null;
            var body = raw[(sep + 4)..];
            return JsonSerializer.Deserialize<TimeResponse>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NtpServer.Sync] 向 {Ip}:{Port} 请求时间失败: {Message}", ip, port, ex.Message);
            return null;
        }
    }

    /// <summary>设备名 → IP（先查已发现列表，再查设置中的手动 IP）</summary>
    public (string? Ip, int Port, string Source) ResolveDevice(string deviceName, string manualIp, int port)
    {
        if (!string.IsNullOrWhiteSpace(manualIp)) return (manualIp.Trim(), port, "手动");
        if (Discovered.TryGetValue(deviceName, out var info)) return (info.Ip, info.Port, "已发现");
        return (null, port, "未找到");
    }
}

/// <summary>HTTP 响应载荷，与 Publish 服务端一致。</summary>
public class TimeResponse
{
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("utcTicks")] public long UtcTicks { get; set; }
    [JsonPropertyName("localIso")] public string LocalIso { get; set; } = "";
    [JsonPropertyName("stratum")] public string Stratum { get; set; } = "1";
    [JsonPropertyName("serverTime")] public DateTime ServerTime { get; set; }

    public DateTime UtcDateTime => new(UtcTicks, DateTimeKind.Utc);
}
