# 真机通信冒烟检查

在 Windows 上连接 Sonic Cube II，关闭正在使用其 MIDI 端口的 SonicCube Panel/Tray 后，从仓库根运行：

```powershell
dotnet run --project tools/device-readback-smoke/Hardware.csproj -c Release
```

程序建立三次独立 MIDI 会话。每次先握手，读取当前 MIC1 音量，然后十次写回同一个值并读取完整预设验证，最后确认 48V 状态与开始时一致。不会切换开关、调用重置或应用启动设置。每次会话有 30 秒上限，读回不一致时立即报错。

结果写入构建输出目录的 `hardware-results.json`，包含握手和写入读回耗时。写入读回包含显式的 70 ms 设备等待，因此并非纯 MIDI 传输耗时。此检查覆盖真实驱动的发送完成、读回和重复打开关闭；热拔插、发送中关闭与驱动错误由 `MidiLifecycleTests` 使用可控驱动模拟覆盖。
