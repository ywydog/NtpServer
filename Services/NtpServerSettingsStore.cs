using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NtpServer.Models;

namespace NtpServer.Services;

/// <summary>
/// 插件所有设置的容器。序列化到单个 JSON 文件中平铺保存。
/// </summary>
public class NtpServerSettingsRoot
{
    public NtpServerSettings NtpServer { get; set; } = new();
    public PublishSettings Publish { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
}

/// <summary>
/// 设置加载与保存（统一）。
/// </summary>
public class NtpServerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<NtpServerSettingsStore>? _logger;

    public NtpServerSettingsStore(ILogger<NtpServerSettingsStore>? logger = null)
    {
        _logger = logger;
    }

    public string SettingsFilePath { get; } = Path.Combine(
        AppContext.BaseDirectory, "Plugins", "NtpServer", "NtpServerSettings.json");

    public NtpServerSettingsRoot Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return new NtpServerSettingsRoot();
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<NtpServerSettingsRoot>(json, JsonOptions)
                   ?? new NtpServerSettingsRoot();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 加载设置失败: {Message}", ex.Message);
            return new NtpServerSettingsRoot();
        }
    }

    public void Save(NtpServerSettingsRoot root)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(root, JsonOptions));
            _logger?.LogInformation("[NtpServer] 设置已保存到 {Path}", SettingsFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 保存设置失败: {Message}", ex.Message);
            throw;
        }
    }
}
