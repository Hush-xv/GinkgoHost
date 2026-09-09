# GinkgoHost

ViewTool **Ginkgo VTG200A USB-I2C 适配器**的 Windows 上位机。交互布局与视觉风格复刻 [Binho Mission Control](https://support.binho.io/getting-started/binho-mission-control/communication-protocols/i2c/)（Settings / Target Device / Transactions 三段式命令面板 + 大面积事务日志），协议引擎使用 ViewTool 官方 `Ginkgo_Driver.dll`。

> 界面：深色 Fluent 主题 · 左侧命令面板 · 右侧实时事务日志 · 底部状态栏

## 功能

### I2C 主机模式

- **连接管理**：适配器扫描/打开/断开，连接状态徽标（导航栏可折叠，底部状态栏实时显示通道/速率/最近事务）
- **Control Mode**：Hardware I2C（通道 0–1）/ Software I2C（GPIO 0–7 模拟，时序固定为 SDK SCTL 例程默认值）
- **Clock Frequency**：100 kHz / 400 kHz / 1 MHz / 1.2 MHz 预设 + Non-Standard Frequencies 开关自定义；连接状态下修改立即生效
- **总线扫描**：0x08–0x77 流式探测（进度实时刷新、命中地址即时出 chip）；当前通道无命中时**自动扫描另一通道**并明确提示
- **地址体系**：7-bit / 8-bit 格式切换，地址文本与扫描 chip 随格式联动换算；chip 悬停显示两种等价形式（如 7 位 `0x49` = 8 位 `0x92`）
- **寄存器/原始读写**：Write Buffer + WRITE、Read Size + READ；Subaddress 留空自动切换为原始读写
- **Transaction Log**：方向徽章（RX 紫 / TX 橙 / SYS 灰）、OK/ERR 状态胶囊、失败行淡红底、斑马纹、双击行复制数据、CSV 导出、5000 条环形上限

### 控制台

内置命令行（控制台页），`↑/↓` 翻历史：

| 命令 | 说明 | 示例 |
|---|---|---|
| `connect` / `disconnect` | 连接/断开适配器 | |
| `adapters` | 适配器数量 | |
| `speed <kHz>` | 修改速率 | `speed 400` |
| `scan` | 扫描从机地址 | |
| `read <addr7> [reg] <len>` | 读，地址 7 位 hex，长度十进制 | `read 49 00 8` |
| `write <addr7> [reg] <b...>` | 写，最多 16 字节 | `write 49 00 de ad` |
| `clear` / `help` | 清屏 / 帮助 | |

### 其他

- **设置持久化**：通道、控制方式、速率、地址格式、上次地址写入 `%APPDATA%\GinkgoHost\settings.json`
- **亮/暗主题**切换
- **无硬件冒烟测试** + 命令行探针（见下文）

## 环境要求

| 项目 | 要求 |
|---|---|
| 硬件 | ViewTool Ginkgo VTG200A（或兼容 USB-I2C 适配器） |
| 驱动 | `Ginkgo_Driver.dll` v2.0.3.6（x64），已随仓库放在 `libs/`，构建自动拷贝 |
| 运行时 | .NET 8 Desktop Runtime（x64），自包含发布可免除 |
| 系统 | Windows 10/11 |

## 构建与运行

```bash
# 调试运行
dotnet build GinkgoHost.csproj -c Debug
GinkgoHost\bin\Debug\net8.0-windows\GinkgoHost.exe

# 单文件发布
dotnet publish GinkgoHost.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 硬件探针（无 GUI 验证）

`tests/SmokeTest` 支持探针模式，绕过 GUI 直接验证总线链路，也可作为自动化自检：

```bash
dotnet run --project tests/SmokeTest -c Debug            # 无硬件冒烟（DLL/入口点/解析器）
dotnet run --project tests/SmokeTest -c Debug --         # + 在线阶段（适配器在位时初始化+扫描）
dotnet run --project tests/SmokeTest -c Debug -- --probe 49 00 1 77 1
#                                                     │  │  │ └┬─ 期望值 └┬─ 通道（缺省 0）
#                                                     │  │  └── 长度        从机 7 位地址
#                                                     │  └─ 寄存器
#                                                     └─ 7 位地址
```

## 地址约定（重要）

- 本工具界面与日志中的从机地址均为 **7 位**（行业惯例），扫描结果亦然
- 数据手册若按 **8 位**标注（含读写位，如 `0x92`），对应 7 位 `0x49`，可在 Address Format 切换后直接填 `92`
- 驱动 `Addr` 参数接收 8 位格式（7 位 `<< 1`），与官方 AT24C02 例程传 `0xA0` 一致

## 架构

```
GinkgoHost/
├── Native/GinkgoDriver.cs     # P/Invoke 封装（签名以官方 SDK C# 例程为准）+ 调试日志
├── Services/
│   ├── I2cService.cs          # 总线服务：SemaphoreSlim 串行化，全部 DLL 调用在后台线程
│   ├── SettingsService.cs     # %APPDATA% 设置持久化
│   └── CommandParser.cs       # 控制台命令解析（纯函数，冒烟测试覆盖）
├── Views/                     # MainWindow（导航+状态栏）、I2cPage、ConsolePage、DevicePage、SettingsPage
├── Models/LogEntry.cs         # 事务记录 + 环形缓冲扩展
├── libs/x64|x86/              # Ginkgo_Driver.dll（构建时按位宽拷贝到输出目录）
└── tests/SmokeTest/           # 冒烟测试 + 硬件探针
```

线程模型：UI 线程零 DLL 调用；所有总线操作经 `SemaphoreSlim` 串行化后在 `Task.Run` 中执行，结果回推 UI。驱动配置为粘性状态（SubAddrWidth 等），服务层在每个操作前按需重新 InitI2C。

## 排障

| 现象 | 处置 |
|---|---|
| 所有读写返回 `EXECUTE_CMD_FAILD (-10)` | 驱动会话卡死——**拔插适配器**即可复位；也确认从机接的通道与 Channel 选择一致 |
| 扫描无命中 | 当前通道无从机时会自动扫另一通道并提示；仍无则检查上拉电阻、共地、从机地址格式（8 位标注需换算） |
| 双击 exe 闪退 | 查看 Windows 事件查看器 → Application → `.NET Runtime` 来源的堆栈；已修复的启动期事件触发问题见提交记录 |
| 驱动 DLL 加载失败 | 确认进程位宽与 `libs/` 下 DLL 位宽一致（当前 x64） |

## 已知限制

- 从机（Slave）模式规划中（驱动 API 已确认：`VII_SlaveReadBytes/WriteBytes` 查询式）
- Internal Pull-Up / Bus Voltage 为 VTG200A 板载跳线控制，无软件接口
- 软件 I2C 速率由 GPIO 时序决定（约 100 kHz 级），不随 Clock Frequency 设置变化

## 致谢

- [ViewTool](http://www.viewtool.com/) — Ginkgo 硬件、驱动与 SDK 例程
- [Binho Mission Control](https://binho.io/) — 交互与视觉参考
- [WPF-UI](https://github.com/lepoco/wpfui) — Fluent 控件库
