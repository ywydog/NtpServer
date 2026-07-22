using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NtpServer.Models;

/// <summary>
/// 设备名/IP 在局域网上发现时交换的最小描述。
/// 同一结构也用于组播与 HTTP 响应载荷。
/// </summary>
public class DeviceInfo
{
    /// <summary>用户设置的设备名（教师便于识别，例如"教室1-主控"）。</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    /// <summary>设备的局域网 IPv4 地址。</summary>
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    /// <summary>HTTP 同步服务端口（默认 12345）。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 12345;

    /// <summary>最近的发布时间（UTC）。</summary>
    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 同步页配置。序列化到 NtpServerSettings.json 中平铺保存。
/// </summary>
public partial class SyncSettings : ObservableObject
{
    /// <summary>目标设备名（用户在同步页填入的、用于标识的"设备名"）。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("targetDeviceName")]
    private string _targetDeviceName = "";

    /// <summary>手动指定的目标 IP（可选，覆盖自动解析）。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("targetIp")]
    private string _targetIp = "";

    /// <summary>HTTP 同步服务端口（必须与发布端一致）。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("port")]
    private int _port = 12345;

    /// <summary>同步后采用的时间调整方式。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("applyMode")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    private TimeApplyMode _applyMode = TimeApplyMode.Soft;

    /// <summary>同步周期（秒）。0 表示仅手动同步。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("autoSyncSeconds")]
    private int _autoSyncSeconds = 0;
}

/// <summary>发布页配置。</summary>
public partial class PublishSettings : ObservableObject
{
    /// <summary>本机作为时间发布者的设备名。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("deviceName")]
    private string _deviceName = Environment.MachineName;

    /// <summary>HTTP 同步服务监听端口。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("port")]
    private int _port = 12345;

    /// <summary>是否自动启动发布服务。</summary>
    [ObservableProperty]
    [property: JsonPropertyName("autoStart")]
    private bool _autoStart = true;
}

/// <summary>把同步到的时间应用到 ClassIsland 的方式。</summary>
public enum TimeApplyMode
{
    /// <summary>软调整：累加 <c>TimeOffsetSeconds</c>，ClassIsland 显示时间会偏移。</summary>
    Soft,
    /// <summary>硬调整：直接修改 Windows 系统时间（需管理员）。</summary>
    Hard
}
