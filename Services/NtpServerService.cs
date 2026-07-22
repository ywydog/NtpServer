using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;
using NtpServer.Models;

namespace NtpServer.Services;

/// <summary>
/// NTP 服务端实现。
/// 注意：本实现只支持 IPv4 监听 + 1:1 简单应答（无扩展字段）。
/// </summary>
public class NtpServerService : IDisposable
{
    private readonly ILogger<NtpServerService> _logger;
    private readonly NtpServerSettings _settings;
    private readonly object _lock = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private long _requestCount;
    private string? _lastError;
    private bool _disposed;

    public NtpServerService(ILogger<NtpServerService> logger, NtpServerSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public string? LastError
    {
        get { lock (_lock) return _lastError; }
    }

    public int Port => _settings.Port;

    /// <summary>
    /// 启动 NTP 服务，监听 <see cref="NtpServerSettings.Port"/>。
    /// 同一实例重复调用将直接返回。
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogDebug("[NtpServer] 服务已在运行中，跳过启动");
                return;
            }
            if (_settings.Port is < 1 or > 65535)
            {
                _lastError = $"端口 {_settings.Port} 非法，必须在 1-65535 之间";
                _logger.LogError("[NtpServer] 端口非法: {Port}", _settings.Port);
                return;
            }
        }

        var localCts = new CancellationTokenSource();
        UdpClient? localClient;
        try
        {
            localClient = new UdpClient(_settings.Port);
        }
        catch (SocketException ex)
        {
            lock (_lock) { _lastError = $"端口 {_settings.Port} 绑定失败: {ex.Message}"; }
            _logger.LogError(ex, "[NtpServer] 无法绑定端口 {Port}: {Message}", _settings.Port, ex.Message);
            localCts.Dispose();
            return;
        }
        catch (Exception ex)
        {
            lock (_lock) { _lastError = $"服务启动失败: {ex.Message}"; }
            _logger.LogError(ex, "[NtpServer] 服务启动失败: {Message}", ex.Message);
            localCts.Dispose();
            return;
        }

        lock (_lock)
        {
            _udpClient = localClient;
            _cts = localCts;
            _isRunning = true;
            _lastError = null;
        }

        _logger.LogInformation("[NtpServer] NTP 服务已启动，监听端口: {Port}", _settings.Port);
        _ = Task.Run(() => ListenAsync(localClient, localCts.Token), localCts.Token);
    }

    /// <summary>
    /// 停止 NTP 服务。重复调用安全。
    /// </summary>
    public void Stop()
    {
        UdpClient? toClose;
        CancellationTokenSource? toCancel;
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            toClose = _udpClient;
            toCancel = _cts;
            _udpClient = null;
            _cts = null;
        }

        try
        {
            toCancel?.Cancel();
            toClose?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NtpServer] 停止服务时发生异常: {Message}", ex.Message);
        }
        finally
        {
            toClose?.Dispose();
            toCancel?.Dispose();
        }
        _logger.LogInformation("[NtpServer] NTP 服务已停止");
    }

    public void Restart()
    {
        _logger.LogInformation("[NtpServer] 正在重启 NTP 服务...");
        Stop();
        Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private async Task ListenAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                // 不在 listen 线程上 await 处理，让循环尽快回到 ReceiveAsync
                _ = Task.Run(() => HandleRequest(client, result), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[NtpServer] 接收数据时发生错误: {Message}", ex.Message);
            }
        }
    }

    private async Task HandleRequest(UdpClient client, UdpReceiveResult result)
    {
        try
        {
            var request = result.Buffer;
            if (request.Length < 48)
            {
                _logger.LogDebug("[NtpServer] 收到无效的 NTP 请求包（长度 {Length}）", request.Length);
                return;
            }

            // M1 fix: 接收/发送必须分开打点，客户端据此计算 delay/dispersion
            var receiveTime = GetCurrentTime();
            var response = BuildNtpResponse(request, receiveTime);
            try
            {
                await client.SendAsync(response, response.Length, result.RemoteEndPoint).ConfigureAwait(false);
                Interlocked.Increment(ref _requestCount);
                _logger.LogDebug("[NtpServer] 已响应来自 {RemoteEndPoint} 的 NTP 请求", result.RemoteEndPoint);
            }
            catch (ObjectDisposedException) { /* 服务已停止 */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NtpServer] 发送响应给 {RemoteEndPoint} 失败: {Message}", result.RemoteEndPoint, ex.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NtpServer] 处理请求时发生错误: {Message}", ex.Message);
        }
    }

    private byte[] BuildNtpResponse(byte[] request, DateTime receiveTime)
    {
        var transmitTime = GetCurrentTime();
        var response = new byte[48];

        // Byte 0: LI(00) + VN(100) + Mode(100) = 0x24 (NTPv4 server)
        response[0] = 0x24;
        // Byte 1: Stratum
        response[1] = (byte)_settings.Stratum;
        // Byte 2: Poll interval (2^6 = 64s)
        response[2] = 6;
        // Byte 3: Precision (-6, 0xFA == -6 in two's complement signed byte)
        response[3] = 0xFA;
        // Byte 4-7: Root Delay (0)
        // Byte 8-11: Root Dispersion (0)
        // Byte 12-15: Reference ID ("LOCL")
        response[12] = (byte)'L';
        response[13] = (byte)'O';
        response[14] = (byte)'C';
        response[15] = (byte)'L';

        var refTimestamp = DateTimeToNtpTimestamp(GetCurrentTime());
        var receiveTimestamp = DateTimeToNtpTimestamp(receiveTime);
        var transmitTimestamp = DateTimeToNtpTimestamp(transmitTime);

        // Byte 16-23: Reference Timestamp
        WriteNtpTimestamp(response, 16, refTimestamp);
        // Byte 24-31: Originate Timestamp (M3 fix: 来自客户端请求的 Transmit, 偏移 40)
        Buffer.BlockCopy(request, 40, response, 24, 8);
        // Byte 32-39: Receive Timestamp (本机接收时刻)
        WriteNtpTimestamp(response, 32, receiveTimestamp);
        // Byte 40-47: Transmit Timestamp (本机发送时刻)
        WriteNtpTimestamp(response, 40, transmitTimestamp);

        return response;
    }

    private DateTime GetCurrentTime()
    {
        if (_settings.TimeSource == NtpTimeSource.ClassIslandTime)
        {
            try
            {
                var exactTimeService = IAppHost.TryGetService<ClassIsland.Core.Abstractions.Services.IExactTimeService>();
                if (exactTimeService != null)
                {
                    var localTime = exactTimeService.GetCurrentLocalDateTime();
                    _logger.LogDebug("[NtpServer] 使用 ClassIsland 精确时间: {Time}", localTime);
                    return localTime.ToUniversalTime();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NtpServer] 获取 ClassIsland 精确时间失败，回退到系统时间");
            }
        }

        return DateTime.UtcNow;
    }

    private static ulong DateTimeToNtpTimestamp(DateTime utcTime)
    {
        var ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var elapsed = utcTime - ntpEpoch;
        var seconds = (uint)elapsed.TotalSeconds;
        var fraction = (uint)((elapsed.TotalSeconds - seconds) * 4294967296.0);
        return ((ulong)seconds << 32) | fraction;
    }

    private static void WriteNtpTimestamp(byte[] buffer, int offset, ulong timestamp)
    {
        buffer[offset] = (byte)(timestamp >> 56);
        buffer[offset + 1] = (byte)(timestamp >> 48);
        buffer[offset + 2] = (byte)(timestamp >> 40);
        buffer[offset + 3] = (byte)(timestamp >> 32);
        buffer[offset + 4] = (byte)(timestamp >> 24);
        buffer[offset + 5] = (byte)(timestamp >> 16);
        buffer[offset + 6] = (byte)(timestamp >> 8);
        buffer[offset + 7] = (byte)timestamp;
    }

    public List<string> GetLocalIpAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork
                               && !IPAddress.IsLoopback(addr.Address)
                               && !addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(addr => addr.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NtpServer] 获取本机 IP 地址失败: {Message}", ex.Message);
            return [];
        }
    }
}
