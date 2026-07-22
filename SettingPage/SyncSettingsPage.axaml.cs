using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Core.Helpers.UI;
using Microsoft.Extensions.Logging;
using NtpServer.Models;
using NtpServer.ViewModels;

namespace NtpServer;

/// <summary>同步页：从目标设备拉取时间并应用。</summary>
[SettingsPageInfo("classisland.ntpServer.sync", "时间同步", "\uE71B", "\uE71B", SettingsPageCategory.External)]
public partial class SyncSettingsPage : SettingsPageBase
{
    private readonly ILogger<SyncSettingsPage>? _logger;
    private readonly DispatcherTimer _refreshTimer;

    public SyncSettingsViewModel ViewModel { get; }

    public SyncSettingsPage(SyncSettingsViewModel viewModel, ILogger<SyncSettingsPage>? logger = null)
    {
        ViewModel = viewModel;
        _logger = logger;
        DataContext = this;
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => ViewModel.Refresh();
        _refreshTimer.Start();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _refreshTimer.Stop();
    }

    private void ButtonStartDiscovery_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.StartDiscovery();
        this.ShowSuccessToast("已启动设备发现");
    }

    private void ButtonStopDiscovery_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.StopDiscovery();
        this.ShowSuccessToast("已停止设备发现");
    }

    private void ButtonUseDevice_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DeviceInfo device) return;
        ViewModel.Settings.TargetDeviceName = device.DeviceName;
        ViewModel.Settings.TargetIp = device.Ip;
        ViewModel.Settings.Port = device.Port;
        this.ShowSuccessToast($"已选中：{device.DeviceName}（{device.Ip}:{device.Port}）");
    }

    private async void ButtonSyncNow_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SyncNowAsync();
            if (string.IsNullOrEmpty(ViewModel.LastError))
            {
                this.ShowSuccessToast($"同步成功：时差 {ViewModel.DeltaSeconds:F2} 秒");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer.Sync] 同步失败");
            this.ShowErrorToast("同步失败", ex);
        }
    }
}
