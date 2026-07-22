using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using NtpServer.Helpers;
using NtpServer.Models;
using NtpServer.Services;

namespace NtpServer.ViewModels;

/// <summary>
/// NTP 设置页视图模型。所有属性都做了"值相同则不触发通知"去重，
/// 避免 2s 定时刷新时无谓地重建 <see cref="ClassIslandAddresses"/>。
/// </summary>
public partial class NtpServerSettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _isRunningAsAdmin;
    [ObservableProperty] private bool _isServiceRunning;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private long _requestCount;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private bool _isNonStandardPort;
    [ObservableProperty] private bool _isPortChanged;
    [ObservableProperty] private string _portWarningMessage = "";
    [ObservableProperty] private InfoBarSeverity _portWarningSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private string? _primaryAddress;
    [ObservableProperty] private string? _lastTestResult;

    /// <summary>
    /// ClassIsland 可直接使用的 NTP 地址列表（http://IP，不带端口）。
    /// 绑定到 <c>ItemsControl</c>，地址变化时整体重建。
    /// </summary>
    public ObservableCollection<AddressItem> ClassIslandAddresses { get; } = new();

    public NtpServerSettings Settings { get; }
    public NtpServerService Service { get; }

    private int _lastKnownPort;

    public NtpServerSettingsViewModel(NtpServerSettings settings, NtpServerService service)
    {
        Settings = settings;
        Service = service;
        _lastKnownPort = settings.Port;

        // 监听设置项变化，端口被改但服务未重启时给用户提示
        settings.PropertyChanged += OnSettingsPropertyChanged;

        RefreshStatus();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NtpServerSettings.Port)) return;
        // 端口变化但未重启 → 提示
        if (Settings.Port != _lastKnownPort)
        {
            IsPortChanged = true;
        }
    }

    /// <summary>
    /// 由 View 在定时器或用户操作时调用。所有属性赋值前都做相等性检查，
    /// 减少 <see cref="INotifyPropertyChanged"/> 触发次数。
    /// </summary>
    public void RefreshStatus()
    {
        // 直接通过生成的属性赋值（避免 MVVMTK0034 直接 ref 字段）
        var admin = AdminHelper.IsRunningInAdmin();
        if (IsRunningAsAdmin != admin) IsRunningAsAdmin = admin;

        var running = Service.IsRunning;
        if (IsServiceRunning != running) IsServiceRunning = running;

        var count = Service.RequestCount;
        if (RequestCount != count) OnPropertyChanged(nameof(RequestCount));

        var err = Service.LastError;
        if (LastError != err) LastError = err;

        var nonStd = Settings.Port != 123;
        if (IsNonStandardPort != nonStd) IsNonStandardPort = nonStd;

        // 端口警告：始终计算，便于 UI 实时反映状态
        var (msg, sev) = BuildPortWarning(Settings.Port, nonStd, admin, running, err);
        if (PortWarningMessage != msg) PortWarningMessage = msg;
        if (PortWarningSeverity != sev) PortWarningSeverity = sev;

        // 地址列表
        var newAddrs = Service.GetLocalIpAddresses()
            .Select(ip => new AddressItem(ip))
            .ToList();
        SyncAddressList(newAddrs);
        var primary = ClassIslandAddresses.FirstOrDefault()?.Value;
        if (PrimaryAddress != primary) PrimaryAddress = primary;

        var newStatus = running
            ? $"NTP 服务正在运行，端口: {Service.Port}"
            : (!string.IsNullOrEmpty(err) ? $"NTP 服务启动失败: {err}" : "NTP 服务未运行");
        if (StatusText != newStatus) StatusText = newStatus;
    }

    /// <summary>
    /// 将 UI 上"已重启服务"标志清零。
    /// </summary>
    public void AcknowledgePortChange()
    {
        _lastKnownPort = Settings.Port;
        IsPortChanged = false;
    }

    private void SyncAddressList(IList<AddressItem> newItems)
    {
        if (ClassIslandAddresses.Count == newItems.Count
            && ClassIslandAddresses.Zip(newItems, (a, b) => a.Value == b.Value).All(x => x))
        {
            return;
        }
        ClassIslandAddresses.Clear();
        foreach (var item in newItems) ClassIslandAddresses.Add(item);
    }

    private static (string Message, InfoBarSeverity Severity) BuildPortWarning(
        int port, bool isNonStandard, bool isAdmin, bool isRunning, string? lastError)
    {
        if (!isRunning && !string.IsNullOrEmpty(lastError)
            && (lastError.Contains("绑定失败", StringComparison.Ordinal)
                || lastError.Contains("socket", StringComparison.OrdinalIgnoreCase)
                || lastError.Contains("access", StringComparison.OrdinalIgnoreCase)))
        {
            return ($"无法绑定端口 {port}：{lastError}。" +
                    (port == 123
                        ? "请以管理员身份重启 ClassIsland，或改为非特权端口（注意：ClassIsland 客户端只支持默认端口 123）。"
                        : "请更换一个未被占用的端口。"),
                    InfoBarSeverity.Error);
        }

        if (port == 123 && !isAdmin)
        {
            return ("当前使用标准 NTP 端口 123，但 ClassIsland 未以管理员身份运行，无法绑定。" +
                    "请以管理员身份重启 ClassIsland，或改为非特权端口。" +
                    "注意：改为非特权端口后，ClassIsland 客户端会忽略地址中的端口（始终向 123 发送），" +
                    "因此只有使用标准端口 123 + 管理员身份，才能让其他 ClassIsland 端连进来。",
                    InfoBarSeverity.Error);
        }

        if (isNonStandard)
        {
            // ClassIsland 客户端调用 NtpClient(host) 时端口硬编码为 123，
            // 即使用户在「时间服务器」框中带 :1234 也会被忽略，因此非标准端口根本不可达。
            return ($"当前端口 {port} 不是标准 NTP 端口（123）。" +
                    "ClassIsland 客户端会忽略地址中的端口（始终使用 123），" +
                    "其他 ClassIsland 端将无法连接到此 NTP 服务。", InfoBarSeverity.Warning);
        }

        if (isRunning)
        {
            return ("正在使用标准 NTP 端口 123，ClassIsland 客户端可直接连接。", InfoBarSeverity.Success);
        }

        return ("", InfoBarSeverity.Informational);
    }

    /// <summary>
    /// 本机回环测试 NTP 服务是否正常响应。
    /// </summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        LastTestResult = "正在测试…";
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            var request = new byte[48];
            // LI(00) + VN(100) + Mode(011) = 0x23 (NTPv4 客户端)
            request[0] = 0x23;
            var target = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Service.Port);
            await udp.SendAsync(request, request.Length, target).ConfigureAwait(false);
            var response = await udp.ReceiveAsync().ConfigureAwait(false);
            LastTestResult = response.Buffer.Length >= 48
                ? $"测试成功：收到 {response.Buffer.Length} 字节响应，Stratum={response.Buffer[1]}"
                : "测试失败：响应包长度不足";
        }
        catch (Exception ex)
        {
            LastTestResult = $"测试失败：{ex.Message}";
        }
    }
}

/// <summary>
/// 用于在 XAML 中绑定显示和复制 ClassIsland 可用地址。
/// 不可变值类型，复制按钮的 CommandParameter 直接绑 Value。
/// </summary>
public record AddressItem(string Value);
