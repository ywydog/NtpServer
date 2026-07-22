using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NtpServer.Helpers;
using NtpServer.Models;
using NtpServer.Services;

namespace NtpServer.ViewModels;

/// <summary>发布页视图模型。</summary>
public partial class PublishSettingsViewModel : ObservableObject
{
    private readonly TimePublishService _service;
    private readonly IExactTimeServiceProvider _timeProvider;
    private readonly ILogger<PublishSettingsViewModel> _logger;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private long _requestCount;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string _statusText = "时间发布服务未运行";
    [ObservableProperty] private IReadOnlyList<string> _localIps = [];
    [ObservableProperty] private string _httpBaseUrl = "";

    public PublishSettings Settings { get; }

    public PublishSettingsViewModel(
        PublishSettings settings,
        TimePublishService service,
        IExactTimeServiceProvider timeProvider,
        ILogger<PublishSettingsViewModel> logger)
    {
        Settings = settings;
        _service = service;
        _timeProvider = timeProvider;
        _logger = logger;
        Refresh();
    }

    public void Refresh()
    {
        IsRunning = _service.IsRunning;
        RequestCount = _service.RequestCount;
        LastError = _service.LastError;
        LocalIps = LocalIpsHelper.GetLocalIpv4Addresses();
        HttpBaseUrl = IsRunning && LocalIps.Count > 0
            ? $"http://{LocalIps[0]}:{Settings.Port}/api/time"
            : "";
        if (IsRunning) StatusText = $"正在发布：设备名「{Settings.DeviceName}」, 端口 {Settings.Port}, 已响应 {RequestCount} 次";
        else if (!string.IsNullOrEmpty(LastError)) StatusText = $"发布服务启动失败：{LastError}";
        else StatusText = "时间发布服务未运行";
    }

    [RelayCommand]
    private void Start()
    {
        _service.Start();
        Refresh();
        _logger.LogInformation("[NtpServer.Publish] 用户点击了「开始发布」");
    }

    [RelayCommand]
    private void Stop()
    {
        _service.Stop();
        Refresh();
        _logger.LogInformation("[NtpServer.Publish] 用户点击了「停止发布」");
    }
}
