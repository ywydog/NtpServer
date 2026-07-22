using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NtpServer.Models;

namespace NtpServer.Services;

/// <summary>
/// NTP 插件设置的加载与保存。
/// 文件位置：在进程基础目录下 <c>Plugins/NtpServer/NtpServerSettings.json</c>。
/// 失败时返回默认实例并记录日志，绝不静默吞掉异常。
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

    /// <summary>设置文件的绝对路径（不保证存在）。</summary>
    public string SettingsFilePath { get; } = Path.Combine(
        AppContext.BaseDirectory, "Plugins", "NtpServer", "NtpServerSettings.json");

    public NtpServerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return new NtpServerSettings();
            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<NtpServerSettings>(json, JsonOptions);
            return settings ?? new NtpServerSettings();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 加载设置失败: {Message}", ex.Message);
            return new NtpServerSettings();
        }
    }

    public void Save(NtpServerSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
            _logger?.LogInformation("[NtpServer] 设置已保存到 {Path}", SettingsFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NtpServer] 保存设置失败: {Message}", ex.Message);
            throw;
        }
    }
}
