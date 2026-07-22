using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Helpers.UI;
using Microsoft.Extensions.Logging;
using NtpServer.ViewModels;

namespace NtpServer;

/// <summary>
/// NTP 服务端设置页。全部状态通过 DI 注入的 <see cref="NtpServerSettingsViewModel"/> 暴露。
/// </summary>
[SettingsPageInfo("classisland.ntpServer", "NTP 时间同步服务端", "\ue770", "\ue771")]
public partial class NtpServerSettingsPage : SettingsPageBase
{
    private readonly ILogger<NtpServerSettingsPage>? _logger;
    private readonly DispatcherTimer _refreshTimer;

    public NtpServerSettingsViewModel ViewModel { get; }

    public NtpServerSettingsPage(
        NtpServerSettingsViewModel viewModel,
        ILogger<NtpServerSettingsPage>? logger = null)
    {
        ViewModel = viewModel;
        _logger = logger;

        DataContext = this;
        InitializeComponent();

        // 定时刷新状态
        _refreshTimer = new DispatcherTimer { Interval = DispatcherPriority.Default };
        _refreshTimer.Tick += (_, _) => ViewModel.RefreshStatus();
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Start();

        _logger?.LogInformation("[NtpServer] 设置页面已加载");
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _refreshTimer.Stop();
    }

    private void ButtonRestartAsAdmin_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 主程序 .exe 路径（自包含发布时 ProcessPath 就是 .exe；dotnet 启动时为 dotnet.exe，需要回退到 argv[0]）
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var argv0 = Environment.GetCommandLineArgs();
                exe = argv0.Length > 0 ? argv0[0] : exe;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Verb = "runas",
                UseShellExecute = true
            };
            // 透传当前主进程参数
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
            {
                psi.ArgumentList.Add(args[i]);
            }

            System.Diagnostics.Process.Start(psi);
            // 等待管理员进程起来再退出，避免主进程先死导致深链打不开
            AppBase.Current.Stop();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 以管理员身份重启失败: {Message}", ex.Message);
            this.ShowErrorToast("无法以管理员身份重启", ex);
        }
    }

    private void ButtonRestartService_OnClick(object? sender, RoutedEventArgs e)
    {
        // 设置会在 PropertyChanged 后由 Plugin 注册的自动保存服务写盘
        ViewModel.Service.Restart();
        ViewModel.AcknowledgePortChange();
        ViewModel.RefreshStatus();
        this.ShowSuccessToast("NTP 服务已重启");
        _logger?.LogInformation("[NtpServer] 用户手动重启了 NTP 服务，端口: {Port}", ViewModel.Settings.Port);
    }

    private void ButtonStopService_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.Service.Stop();
        ViewModel.RefreshStatus();
        this.ShowSuccessToast("NTP 服务已停止");
        _logger?.LogInformation("[NtpServer] 用户手动停止了 NTP 服务");
    }

    private async void ButtonCopyAddress_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.CommandParameter is not string address || string.IsNullOrEmpty(address)) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(address);
                this.ShowSuccessToast($"已复制: {address}");
                _logger?.LogDebug("[NtpServer] 用户复制了地址: {Address}", address);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 复制到剪贴板失败: {Message}", ex.Message);
            this.ShowErrorToast("复制到剪贴板失败", ex);
        }
    }

    private async void ButtonCopyPrimaryAddress_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.CommandParameter is not string address || string.IsNullOrEmpty(address)) return;
        // 复用通用复制逻辑
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(address);
                this.ShowSuccessToast($"已复制推荐地址: {address}");
                _logger?.LogDebug("[NtpServer] 用户复制了推荐地址: {Address}", address);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 复制到剪贴板失败: {Message}", ex.Message);
            this.ShowErrorToast("复制到剪贴板失败", ex);
        }
    }
}
