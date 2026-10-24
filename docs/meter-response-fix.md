# 电平响应修复验证

> 本文记录首版响应修复。后续试用发现小幅上升仍呈阶梯，本轮已调整上升算法；当前实现与验证结果见 [电平上升平滑修正](meter-smoothing-fix.md)。下列 25–33 ms 与性能数据仅适用于首版。

基准为 `f86ae35186ee615eaaff33bb285d9cf25ea6b6df`。本轮根据电平整体落后于声音的反馈调整上升响应，保留窗口、布局、主题和 DPI 行为。

## 修改范围

- `MeterBar` 上升速度从 240 调整为 2400 dB/s，仍由现有逐帧渲染驱动，不增加定时器或刷新频率。
- 条体使用最新输入，分别保留左右声道在两帧之间收到的最大值，供峰值线显示；过期的待绘峰值不在恢复绘制时重新获得保持时间。
- 条体回落维持 24 dB/s，峰值保持维持 900 ms，峰值回落维持 10 dB/s。静态位图缓存、动态 Visual 和静音/稳定电平休眠机制保持原有实现。
- `PipeConnection.Post` 为电平保留一个最新待发值；写入阻塞时最多一个正在发送值和一个待发值。命令、状态、响应继续使用可靠 FIFO。服务器原有 40 ms 合并调度与 IPC 字段不变。
- 应用层合并不能撤回已经写入操作系统管道缓冲的数据。短峰值保留覆盖已经交付给控件的样本，不能恢复硬件未上报或发送前已合并掉的样本。

## 响应时间

以下由真实控件的动画推进函数按固定帧间隔测得，是空条上升到目标的动画时间，**不是声音到屏幕的端到端延迟**。

| 帧率 | −12 dB 修改前 | −12 dB 修改后 | 0 dB 修改前 | 0 dB 修改后 |
|---|---:|---:|---:|---:|
| 60 Hz | 200.00 ms | 33.33 ms | 250.00 ms | 33.33 ms |
| 144 Hz | 201.39 ms | 20.83 ms | 250.00 ms | 27.78 ms |
| 240 Hz | 200.00 ms | 20.83 ms | 250.00 ms | 25.00 ms |

此前只读真机诊断的 8 秒样本共收到 92 帧，设备上报间隔中位数 86.66 ms，p95 为 90.26 ms，最长 103.94 ms。这一上报周期不会因动画修复而缩短；本轮尚未完成真人声源与屏幕同时录制的同步验证。

## 自动回归与发布

```powershell
dotnet test SonicCubePanel.slnx -c Release --no-restore
dotnet publish SonicCube.Tray/SonicCube.Tray.csproj -c Release --no-restore --self-contained false -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/meter-response-fix
dotnet publish SonicCube.Panel/SonicCube.Panel.csproj -c Release --no-restore --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/meter-response-fix
dotnet publish SonicCube.Tray/SonicCube.Tray.csproj -c Release --no-restore -o out/meter-response-fix-native
```

Release 测试 **180 项通过、0 失败、0 跳过**（Protocol/Shared/Tray 65，Panel 115）。相对基准新增 14 项用例，覆盖：

- 60/144/240 Hz 上升时间，零/极小时间步，原有回落、峰值保持与静音收敛。
- 同一帧之前的高低输入、各声道独立短峰、峰值单独触发绘制、过期峰值、较新的低峰以及稳定电平下降后完整保持。
- 10,000 次电平合并、真实未读管道的背压、恢复后的最终值、可靠消息顺序、待发电平期间关闭及重复关闭。

Panel 与 Tray 的框架依赖单文件构建均成功，配套试用文件位于 `out/meter-response-fix/`。Tray NativeAOT 发布也成功，位于 `out/meter-response-fix-native/`。现有 `MSTEST0037` 测试断言风格提示未影响通过。

试用时退出旧 Panel 和托盘程序，再从 `out/meter-response-fix/` 启动 `SonicCube.Panel.exe`，使 Panel 与 Tray 都使用本次修复。

## 性能验证方法

可复现工具见 [基准说明](../tools/meter-response-benchmark/README.md)。对比基准和当前源码中的五个真实 WPF 电平控件，双方都预热静态缓存、更新保留的子 Visual。输入间隔 87 ms，分别模拟 60/144/240 Hz；每种帧率预热 240 秒，再交错执行七轮、每轮 2400 秒的模拟帧。

两版均使用相同的确定性时钟替换 `DateTime.UtcNow`，以使快速模拟中的接收时间与动画时间一致；计时不包含墙钟读取。采样数据预先生成，反射仅用于初始化，测量阶段调用编译委托。此测试衡量托管输入处理、动画推进和 Visual 更新的耗时与线程分配，不能换算成整个程序的 CPU/GPU 占用或真实显示帧率。

2026-10-24 最终测量结果如下，耗时为七轮中位数，括号为最小/最大值；每项均包含五个双声道电平表的一帧工作。

| 帧率 | 修改前耗时 µs | 修改后耗时 µs | 修改前分配 B | 修改后分配 B |
|---|---:|---:|---:|---:|
| 60 Hz | 2.693（2.588–2.786） | 2.684（2.608–2.824） | 354.10 | 336.12 |
| 144 Hz | 2.581（2.560–2.609） | 2.584（2.489–2.727） | 341.50 | 317.29 |
| 240 Hz | 2.595（2.544–2.708） | 2.504（2.495–2.644） | 337.72 | 311.87 |

耗时范围重叠，未见明显托管计算回退；该输入轨迹下每帧分配减少约 5.1% / 7.1% / 7.7%，七轮分配结果一致。未直接测量 GPU 占用，也未据此断言整体 CPU/GPU 占用不变。完整原始结果保存在 `tools/meter-response-benchmark/bin/Release/net10.0-windows/results.json`，可用上述工具重新生成。

九组相同显示/峰值状态的逐像素比较全部一致，覆盖原有 100%/150%/200% DPI 输出；这是原绘制回归验证，本轮没有添加 DPI 适配。
