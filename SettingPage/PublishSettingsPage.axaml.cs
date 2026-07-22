using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Helpers.UI;
using Microsoft.Extensions.Logging;
using NtpServer.ViewModels;

namespace NtpServer;

/// <summary>发布页：把本机时间作为时间源发布到局域网。</summary>
[SettingsPageInfo("classisland.ntpServer.publish", "时间发布", "\uE724")]
public partial class PublishSettingsPage : SettingsPageBase
{
    private readonly ILogger<PublishSettingsPage>? _logger;
    private readonly DispatcherTimer _refreshTimer;

    public PublishSettingsViewModel ViewModel { get; }

    public PublishSettingsPage(PublishSettingsViewModel viewModel, ILogger<PublishSettingsPage>? logger = null)
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

    private void ButtonStartPublish_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.Start();
        this.ShowSuccessToast("已开始发布");
    }

    private void ButtonStopPublish_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.Stop();
        this.ShowSuccessToast("已停止发布");
    }

    private async void ButtonCopyUrl_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string ip) return;
        var url = $"http://{ip}:{ViewModel.Settings.Port}/api/time";
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(url);
                this.ShowSuccessToast($"已复制: {url}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer.Publish] 复制 URL 失败");
            this.ShowErrorToast("复制失败", ex);
        }
    }
}
