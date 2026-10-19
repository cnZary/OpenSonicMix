# SonicCube Panel

Sonicake **Sonic Cube II** 声卡的 Windows 控制软件，用于替代官方 Sonic Mix 面板。

本项目完全基于逆向分析所得的私有 SysEx 协议独立实现，**不依赖官方 Sonic Mix.exe 或 HTUSBTools.dll，也不引入任何第三方 MIDI 库**(直接封装 WinMM)。

## 项目简介

- 双进程架构:**Tray** 常驻托盘独占 MIDI 设备，**Panel** 按需启动的完整控制面板。
- 两种 MIDI 工作模式(低占用 / 快速连接)，托盘菜单动态切换。
- 完整控制面板：输入通道、回放通道、效果区、总线、电平表、设置页。
- 真实设备协议验证控制台(DeviceConsole)，以及协议、IPC 与 WPF 电平动画回归测试。
- 中英文双语本地化(`zh-CN` / `en-US`)。

## 架构

```
SonicCube.Panel (WPF + 内置 Fluent 主题, 按需启动)
      │  Named Pipe IPC
      ▼
SonicCube.Tray (NativeAOT 原生进程, 长期驻留)
      │  WinMM SysEx
      ▼
Sonic Cube II 设备
```

- **Tray 独占 MIDI**:Panel 不直接持有 MIDI 端口，一切设备操作(读写参数、切效果、开关 48V 等)经 Named Pipe IPC 转交 Tray 执行。
- **Panel 关闭即退出**:不影响 Tray 驻留与设备状态。
- **单实例**:Tray 通过 Win32 Mutex 保证单实例运行。

## 项目结构与功能

| 项目 | 说明 |
|---|---|
| `SonicCube.Protocol/` | 纯协议库：SysEx 帧编解码、命令地址表(`Commands/`)、183 字节分片重组(`Frames/`)、预设/全局状态模型(`Models/PresetState.cs`、`GlobalState.cs`)、数值换算(`ValueMapping.cs`)、15 算法 70 参数效果表(`Models/FxCatalog.cs`)。 |
| `SonicCube.Shared/` | NativeAOT 兼容的 WinMM MIDI 封装(`Midi/WinMM.cs`、`MidiDevice.cs`)、高层会话(`Midi/DeviceSession.cs`，含握手重试、float 参数读回验证)、IPC 消息模型与管道封装(`Ipc/`)。 |
| `SonicCube.Tray/` | NativeAOT 原生托盘进程:Win32 托盘、单实例 Mutex、异步命令协调器 `DeviceCoordinator`、双工作模式 `WorkModeManager`、启动/重连自动动作、Named Pipe IPC 服务端、滚动日志(`Logging/Logger.cs`)、Windows 自启(`Startup/StartupManager.cs`)。 |
| `SonicCube.Panel/` | WPF 完整控制面板(官方内置 Fluent 主题):输入通道 MIC1 / LINE-INST2，回放 USB / OTG，效果区 COMP / GlobalEQ / INST / EQ / HPF / RVB，总线，电平表，设置页。 |
| `SonicCube.Protocol.Tests/` | 协议与 IPC 单元测试(MSTest)。 |
| `SonicCube.Panel.Tests/` | WPF 电平表动画回归测试，覆盖极短帧间隔、平滑速度与峰值保持。 |
| `SonicCube.DeviceConsole/` | 真实设备协议验证控制台(子命令 `enum` / `status` / `efx` / `p48` / `raw`)。 |

托盘菜单提供六个快速开关:**48V / EFX / MONITOR / ONAIR / 总静音 / MONO**。

## MIDI 工作模式

Tray 支持两种工作模式，可在托盘菜单中动态切换:

| | 低占用模式 | 快速连接模式(默认) |
|---|---|---|
| MIDI 连接 | 按需连接 | 保持连接 |
| 空闲释放 | 60s 无操作自动断开 | 不释放 |
| 物理拔插重连 | 不监听 | 自动重连 |
| 重连自动动作 | 不可用(设置项置灰) | 可用 |

### 自动动作

- **启动自动动作**:仅 **48V** 与 **EFX** 两项，可独立开关。其中 48V(幻象电源)涉及麦克风硬件安全，需用户在设置中显式授权(`phantomPowerConsent`)才会在启动时自动开启。
- **设备重连自动动作**:独立总开关 `reapplyOnReconnect`(默认开),**仅快速连接模式有效**;低占用模式下设置项置灰但配置保留。

## 协议要点(供维护者参考)

- 传输:WinMM SysEx;帧头 magic 为 `"!%~JUSH"`;负载采用 nibble 编码 + 7bit 长度;大包按 183 字节分片重组。
- 握手流程:`SOFTWARE_STATE` → 整包预设 → 全局状态。

### 已实测的设备行为(重要)

1. **float 参数写入不回确认包**:PHASE / SEND / VOLPOT / FX 参数等写入后设备不回复，必须**发送后主动读回验证**(`DeviceSession` 已内置该逻辑)。
2. **写入与当前值相同可能不回包**:重发相同值不能作为确认手段。
3. **电平包高频推送**:电平消息(`0x000B0011`)约每 87ms 推送一次，接收端需限频处理，避免 UI/IPC 洪泛。

## 构建与运行

需要 **.NET 10 SDK**。Tray 的 NativeAOT 发布另需 **MSVC 工具链 + Windows SDK**(Visual Studio C++ 工作负载)。

```powershell
# 全部构建 (build all)
dotnet build SonicCubePanel.slnx

# 协议、IPC 与 WPF 电平动画回归测试
dotnet test SonicCubePanel.slnx

# Tray NativeAOT 发布 (native exe, no runtime dependency)
dotnet publish SonicCube.Tray/SonicCube.Tray.csproj -c Release -r win-x64

# Panel 单文件发布 (framework-dependent single file)
dotnet publish SonicCube.Panel/SonicCube.Panel.csproj -c Release -r win-x64
```

### 交互模拟模式

运行 `publish\SonicCube.Panel.exe --simulate` 可预览本地模拟设备；模拟与正式模式各自保持单实例，可以同时打开。
HPF、效果开关、算法参数、推子、静音/独听和重置均会更新模拟状态；设置只在本次预览中保留。
10 路电平使用不同的变化节奏，每 8 秒错峰短暂达到 0 dB，便于观察顶部红色填充和峰值警告。
模拟模式不连接 MIDI 设备，窗口最小化或隐藏时暂停电平推送。

### 真实设备验证(DeviceConsole)

```powershell
dotnet run --project SonicCube.DeviceConsole -- enum       # 枚举 MIDI 端口
dotnet run --project SonicCube.DeviceConsole -- status     # 读取设备状态
dotnet run --project SonicCube.DeviceConsole -- efx on|off # 开关效果
dotnet run --project SonicCube.DeviceConsole -- p48 on|off # 开关 48V
dotnet run --project SonicCube.DeviceConsole -- raw        # 原始收发
```

> **注意**:验证前请先关闭官方 Sonic Mix —— 它会独占 MIDI 输入端口。

### 发布目录约定

最终发布产物置于 `publish/`:

```
publish/
├── SonicCube.Tray.exe    # NativeAOT 原生 exe, 无运行时依赖
├── SonicCube.Panel.exe   # 框架依赖单文件(WPF + 内置 Fluent), 需 .NET 10 Desktop Runtime
└── zh-CN/                # 中文本地化卫星资源(en-US 为默认,内嵌主程序集)
```

### 运行要求

- `SonicCube.Tray.exe`:原生 exe，无依赖。
- `SonicCube.Panel.exe`:需安装 **.NET 10 Desktop Runtime**(框架依赖发布)。

## 配置与日志

- 配置文件:`%AppData%\SonicCubePanel\settings.json`
- 托盘日志:`%AppData%\SonicCubePanel\logs\tray.log`(滚动，10MB × 5 份)

## 已知限制 / 不在本期范围

- **固件升级**:未实现，不在本期范围。
- **采样率调整**:未实现，不在本期范围。
- 仅支持 Windows(依赖 WinMM 与 Win32 托盘)。
- Panel 与 Tray 必须同机运行(Named Pipe IPC，不支持远程)。
