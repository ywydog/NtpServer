using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NtpServer.Helpers;
using NtpServer.Models;
using NtpServer.Services;

namespace NtpServer.ViewModels;

/// <summary>同步页视图模型。</summary>
public partial class SyncSettingsViewModel : ObservableObject
{
    private readonly TimeSyncClient _client;
    private readonly IExactTimeServiceProvider _timeProvider;
    private readonly Func<ClassIsland.Core.Abstractions.Services.IExactTimeService> _ciTimeServiceAccessor;
    private readonly ILogger<SyncSettingsViewModel> _logger;

    [ObservableProperty] private bool _isDiscoveryRunning;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string _statusText = "尚未同步";
    [ObservableProperty] private string? _lastSyncedDevice;
    [ObservableProperty] private DateTime? _lastSyncedTime;
    [ObservableProperty] private double? _deltaSeconds;
    [ObservableProperty] private string _resolvedEndpoint = "";

    public ObservableCollection<DeviceInfo> DiscoveredDevices { get; } = new();
    public SyncSettings Settings { get; }

    public SyncSettingsViewModel(
        SyncSettings settings,
        TimeSyncClient client,
        IExactTimeServiceProvider timeProvider,
        Func<ClassIsland.Core.Abstractions.Services.IExactTimeService> ciTimeServiceAccessor,
        ILogger<SyncSettingsViewModel> logger)
    {
        Settings = settings;
        _client = client;
        _timeProvider = timeProvider;
        _ciTimeServiceAccessor = ciTimeServiceAccessor;
        _logger = logger;

        _client.Discovered.CollectionChanged += OnDiscoveredChanged;
    }

    private void OnDiscoveredChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 简单整表刷新（设备数少，可接受）
        DiscoveredDevices.Clear();
        foreach (var kv in _client.Discovered.OrderByDescending(d => d.Value.LastSeen))
        {
            DiscoveredDevices.Add(kv.Value);
        }
    }

    public void Refresh()
    {
        IsDiscoveryRunning = _client.IsDiscoveryRunning;
        _client.PruneStale();
    }

    [RelayCommand]
    private void StartDiscovery()
    {
        _client.StartDiscovery();
        Refresh();
        _logger.LogInformation("[NtpServer.Sync] 启动设备发现");
    }

    [RelayCommand]
    private void StopDiscovery()
    {
        _client.StopDiscovery();
        Refresh();
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        LastError = null;
        var (ip, port, source) = _client.ResolveDevice(
            Settings.TargetDeviceName, Settings.TargetIp, Settings.Port);
        if (string.IsNullOrEmpty(ip))
        {
            LastError = $"未找到设备「{Settings.TargetDeviceName}」";
            ResolvedEndpoint = "";
            _logger.LogWarning("[NtpServer.Sync] 设备解析失败：{Name}", Settings.TargetDeviceName);
            return;
        }
        ResolvedEndpoint = $"http://{ip}:{port}/api/time（来源：{source}）";

        var response = await _client.RequestAsync(ip, port).ConfigureAwait(false);
        if (response == null)
        {
            LastError = "请求超时或响应解析失败";
            _logger.LogWarning("[NtpServer.Sync] 请求失败 {Ip}:{Port}", ip, port);
            return;
        }

        var ciTime = _timeProvider.GetCurrentLocalDateTime();
        var serverTime = response.UtcDateTime.ToLocalTime();
        var delta = (serverTime - ciTime).TotalSeconds;
        var ok = ApplyDelta(delta);
        LastSyncedTime = serverTime;
        LastSyncedDevice = response.DeviceName;
        DeltaSeconds = delta;
        StatusText = ok
            ? $"同步成功：服务端时间 {serverTime:yyyy-MM-dd HH:mm:ss}，与本机相差 {delta:F2} 秒"
            : $"同步完成但时间未应用（查看上方提示）";
        _logger.LogInformation("[NtpServer.Sync] 同步完成 {Device}, delta={Delta}", response.DeviceName, delta);
    }

    private bool ApplyDelta(double deltaSeconds)
    {
        switch (Settings.ApplyMode)
        {
            case TimeApplyMode.Soft:
                try
                {
                    var ci = _ciTimeServiceAccessor();
                    ci.TimeOffsetSeconds += deltaSeconds;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "软调整失败：" + ex.Message;
                    _logger.LogError(ex, "[NtpServer.Sync] 软调整失败");
                    return false;
                }
            case TimeApplyMode.Hard:
                if (!AdminHelper.IsRunningInAdmin())
                {
                    LastError = "硬调整需要管理员身份，请在「服务端」页中以管理员身份重启。";
                    return false;
                }
                try
                {
                    var target = _timeProvider.GetCurrentLocalDateTime().AddSeconds(deltaSeconds);
                    if (SystemClockHelper.ApplyHardTime(target, out var err))
                    {
                        // 系统时间被改了，把 ClassIsland 偏移归零
                        var ci = _ciTimeServiceAccessor();
                        ci.TimeOffsetSeconds = 0;
                        return true;
                    }
                    LastError = "硬调整失败：" + err;
                    return false;
                }
                catch (Exception ex)
                {
                    LastError = "硬调整失败：" + ex.Message;
                    return false;
                }
            default:
                return false;
        }
    }
}
