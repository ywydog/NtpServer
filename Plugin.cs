using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NtpServer.Models;
using NtpServer.Services;

namespace NtpServer;

/// <summary>
/// 插件入口。负责注册服务、设置页和生命周期事件。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 设置存储：作为单例，整个进程共用一个文件路径
        services.AddSingleton<NtpServerSettingsStore>();

        // 单实例设置对象，所有 View 共用
        services.AddSingleton<NtpServerSettings>(sp =>
        {
            var store = sp.GetRequiredService<NtpServerSettingsStore>();
            return store.Load();
        });

        // NTP 服务：单例，接收 DI 注入的 logger 与 settings
        services.AddSingleton<NtpServerService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<NtpServerService>>();
            var settings = sp.GetRequiredService<NtpServerSettings>();
            return new NtpServerService(logger, settings);
        });

        // 设置页：DI 自动注入 ViewModel 与依赖
        services.AddSettingsPage<NtpServerSettingsPage>();

        // 生命周期：AppStarted 自动启动；AppStopping 自动停止
        AppBase.Current.AppStarted += (_, _) =>
        {
            var logger = IAppHost.TryGetService<ILogger<Plugin>>();
            try
            {
                IAppHost.GetService<NtpServerService>().Start();
                logger?.LogInformation("[NtpServer] 应用已启动，NTP 服务自动启动完成");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[NtpServer] 自动启动 NTP 服务失败: {Message}", ex.Message);
            }
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            var logger = IAppHost.TryGetService<ILogger<Plugin>>();
            try
            {
                IAppHost.TryGetService<NtpServerService>()?.Stop();
                logger?.LogInformation("[NtpServer] 应用正在停止，NTP 服务已停止");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[NtpServer] 停止 NTP 服务时发生异常: {Message}", ex.Message);
            }
        };
    }
}
