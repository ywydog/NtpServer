# NTP 时间同步服务端（多设备）

[![GitHub](https://img.shields.io/badge/GitHub-%23121011.svg?logo=github&logoColor=white)](https://github.com/ywydog/NTPPlugin)

> ClassIsland 局域网时间同步插件套件。
> 把任意一台 ClassIsland 设为「时间发布端」，
> 其他 ClassIsland 设为「时间同步端」即可对齐时间。

> [!WARNING]
> - 此插件目前仅适用于 Windows 平台。
> - 使用标准 NTP 端口（UDP 123）需要以管理员身份运行 ClassIsland。
> - **ClassIsland 客户端「精确时间」中的「时间服务器」框只接受纯 IP 或主机名**，不要带 `http://`、不要带端口。
> - **ClassIsland 客户端会忽略地址中的端口（始终向 123 发送）**，因此必须让本插件监听 **123 端口** + **以管理员身份运行** 才能让其他 ClassIsland 端连进来。

## 三个设置页

| 页面 | 作用 |
|---|---|
| **时间发布** | 把本机时间通过 HTTP + 组播发布到局域网 |
| **时间同步** | 通过「设备名」或「IP」从发布端拉取时间并应用到本机 |
| **NTP 服务端** | 标准 NTP 协议（123 端口），供使用 ClassIsland 自带「精确时间」功能的客户端接入 |

## 时间发布端

1. 打开「时间发布」页
2. 设置**设备名**（例如"教室1-主控"），其他 ClassIsland 端会按设备名发现你
3. 设定端口（默认 12345，**所有发布端必须相同**）
4. 打开「自动启动」后，应用启动时即开始发布
5. 也可以手动「开始发布」

发布端会：
- 在 `0.0.0.0:<port>` 上监听 HTTP `GET /api/time`，返回 `{ deviceName, utcTicks, localIso, serverTime }`
- 每 5 秒向组播 `239.255.42.42:47492` 发送 `{ deviceName, ip, port }`

## 时间同步端

1. 打开「时间同步」页
2. 点击「启动发现」订阅组播，所有发布端会出现在「设备发现」列表中
3. 在「设备发现」中点「使用此设备」，或直接在「设备名」中输入对方设备名
4. 必要时填入「手动 IP」（覆盖自动解析）
5. 选择时间调整方式：
   - **软调整**（推荐）：仅在 ClassIsland 内部累加 `TimeOffsetSeconds`，无需管理员
   - **硬调整**：直接调用 `kernel32!SetSystemTime` 修改 Windows 系统时间（需管理员）
6. 点击「立即同步」

## 时间调整方式对比

| 方式 | 是否改系统时间 | 是否需管理员 | 重启后是否保留 | 适用场景 |
|---|---|---|---|---|
| 软调整 | ❌ | ❌ | ❌（归零） | 临时对齐 |
| 硬调整 | ✅ | ✅ | ✅ | 永久对齐 |

> 软调整 = 修改 `ClassIsland.Settings.TimeOffsetSeconds`，
> ClassIsland 后续显示的时间将自动加上偏移；
> ClassIsland 进程重启后偏移归零。
>
> 硬调整会立即修改 Windows 系统时间，并把偏移清零。
> 系统中其他依赖系统时间的程序也会同步偏移。

## 声明

- 该插件仅适用于 Windows。
- **这个插件适用于 ClassIsland 2.x（≥2.0.0.1）版本。**
- GPLv3 许可。[LICENSE](./LICENSE)
