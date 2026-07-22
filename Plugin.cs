using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NtpServer.Models;
using NtpServer.Services;
using NtpServer.ViewModels;
using System.ComponentModel;

namespace NtpServer;

/// <summary>插件入口。</summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>把 ClassIsland 自带的 IExactTimeService 适配到我们的 IExactTimeServiceProvider。</summary>
    private class CiExactTimeProvider : IExactTimeServiceProvider
    {
        private readonly IExactTimeService _ci;
        public CiExactTimeProvider(IExactTimeService ci) { _ci = ci; }
        public DateTime GetCurrentLocalDateTime() => _ci.GetCurrentLocalDateTime();
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 1) 设置根（所有页共用）
        services.AddSingleton<NtpServerSettingsStore>();
        services.AddSingleton<NtpServerSettingsRoot>(sp =>
            sp.GetRequiredService<NtpServerSettingsStore>().Load());
        services.AddSingleton<NtpServerSettings>(sp => sp.GetRequiredService<NtpServerSettingsRoot>().NtpServer);
        services.AddSingleton<PublishSettings>(sp => sp.GetRequiredService<NtpServerSettingsRoot>().Publish);
        services.AddSingleton<SyncSettings>(sp => sp.GetRequiredService<NtpServerSettingsRoot>().Sync);

        // 自动保存：监听三个设置对象的 PropertyChanged，1 秒去抖后写盘
        services.AddHostedService(sp =>
        {
            var store = sp.GetRequiredService<NtpServerSettingsStore>();
            var root = sp.GetRequiredService<NtpServerSettingsRoot>();
            var saverLogger = sp.GetRequiredService<ILogger<Plugin>>();
            var pending = new System.Collections.Generic.HashSet<INotifyPropertyChanged>();
            var timer = new System.Timers.Timer(1000) { AutoReset = false };
            timer.Elapsed += (_, _) =>
            {
                try { store.Save(root); pending.Clear(); }
                catch (Exception ex) { saverLogger.LogError(ex, "[NtpServer] 自动保存设置失败"); }
            };
            void Hook(INotifyPropertyChanged s)
            {
                s.PropertyChanged += (_, _) =>
                {
                    pending.Add(s);
                    timer.Stop(); timer.Start();
                };
            }
            Hook(root.NtpServer); Hook(root.Publish); Hook(root.Sync);
            return new NoopHostedService(() => { timer.Stop(); pending.Clear(); });
        });

        // 2) IExactTimeService 桥接
        services.AddSingleton<IExactTimeServiceProvider>(sp =>
            new CiExactTimeProvider(sp.GetRequiredService<IExactTimeService>()));

        // 3) NTP 标准端口服务端（123 端口，原有功能保留）
        services.AddSingleton<NtpServerService>(sp =>
            new NtpServerService(
                sp.GetRequiredService<ILogger<NtpServerService>>(),
                sp.GetRequiredService<NtpServerSettings>()));

        // 4) 同步/发布的发现 + HTTP
        services.AddSingleton<TimePublishService>(sp =>
            new TimePublishService(
                sp.GetRequiredService<ILogger<TimePublishService>>(),
                sp.GetRequiredService<IExactTimeServiceProvider>(),
                () => sp.GetRequiredService<PublishSettings>()));
        services.AddSingleton<TimeSyncClient>(sp =>
            new TimeSyncClient(
                sp.GetRequiredService<ILogger<TimeSyncClient>>(),
                sp.GetRequiredService<IExactTimeServiceProvider>(),
                () => sp.GetRequiredService<SyncSettings>()));

        // 5) ViewModel
        services.AddSingleton<ViewModels.NtpServerSettingsViewModel>(sp =>
            new ViewModels.NtpServerSettingsViewModel(
                sp.GetRequiredService<NtpServerSettings>(),
                sp.GetRequiredService<NtpServerService>()));
        services.AddSingleton<PublishSettingsViewModel>(sp =>
            new PublishSettingsViewModel(
                sp.GetRequiredService<PublishSettings>(),
                sp.GetRequiredService<TimePublishService>(),
                sp.GetRequiredService<IExactTimeServiceProvider>(),
                sp.GetRequiredService<ILogger<PublishSettingsViewModel>>()));
        services.AddSingleton<SyncSettingsViewModel>(sp =>
            new SyncSettingsViewModel(
                sp.GetRequiredService<SyncSettings>(),
                sp.GetRequiredService<TimeSyncClient>(),
                sp.GetRequiredService<IExactTimeServiceProvider>(),
                () => sp.GetRequiredService<IExactTimeService>(),
                sp.GetRequiredService<ILogger<SyncSettingsViewModel>>()));

        // 6) 三个设置页
        services.AddSettingsPage<NtpServerSettingsPage>();
        services.AddSettingsPage<PublishSettingsPage>();
        services.AddSettingsPage<SyncSettingsPage>();

        // 7) 生命周期
        AppBase.Current.AppStarted += (_, _) =>
        {
            var logger = IAppHost.TryGetService<ILogger<Plugin>>();
            try
            {
                IAppHost.GetService<NtpServerService>().Start();
                var publish = IAppHost.TryGetService<TimePublishService>();
                var publishSettings = IAppHost.TryGetService<PublishSettings>();
                if (publish != null && publishSettings?.AutoStart == true) publish.Start();
                var sync = IAppHost.TryGetService<TimeSyncClient>();
                sync?.StartDiscovery();
                logger?.LogInformation("[NtpServer] 应用已启动，NTP 与发布服务自启完成");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[NtpServer] 自启失败: {Message}", ex.Message);
            }
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            var logger = IAppHost.TryGetService<ILogger<Plugin>>();
            try
            {
                IAppHost.TryGetService<TimeSyncClient>()?.StopDiscovery();
                IAppHost.TryGetService<TimePublishService>()?.Stop();
                IAppHost.TryGetService<NtpServerService>()?.Stop();
                IAppHost.TryGetService<TimeSyncClient>()?.Dispose();
                IAppHost.TryGetService<TimePublishService>()?.Dispose();
                IAppHost.TryGetService<NtpServerService>()?.Dispose();

                // 持久化设置
                var store = IAppHost.TryGetService<NtpServerSettingsStore>();
                var root = IAppHost.TryGetService<NtpServerSettingsRoot>();
                if (store != null && root != null) store.Save(root);

                logger?.LogInformation("[NtpServer] 应用正在停止，所有服务已停止并保存设置");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[NtpServer] 停止时发生异常: {Message}", ex.Message);
            }
        };
    }
}

/// <summary>什么都不做，只是把 Action 包成 IHostedService 用于作用域内的资源释放。</summary>
internal class NoopHostedService : IHostedService
{
    private readonly Action _onStop;
    public NoopHostedService(Action onStop) { _onStop = onStop; }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) { _onStop(); return Task.CompletedTask; }
}
