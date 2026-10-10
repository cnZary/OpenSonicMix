using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Midi;

namespace SonicCube.DeviceConsole;

/// <summary>
/// 阶段一真实设备协议验证控制台。
/// 子命令:
///   enum            枚举 Sonic Cube II MIDI 输入/输出端口
///   status          打开端口、握手、读取 0x00000011 全局与 0x00000010 预设并打印
///   efx on|off      验证 EFX 开启/关闭并等待回包
///   p48 on|off      在显式安全确认后验证 48V(幻象电源,有损坏设备风险)
///   raw             打印原始收发帧(配合 status 流程)
/// </summary>
internal static class Program
{
    private static bool _raw;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var argList = args.ToList();
        _raw = argList.Remove("--raw");
        string cmd = argList.Count > 0 ? argList[0].ToLowerInvariant() : "enum";

        try
        {
            return cmd switch
            {
                "enum" => EnumDevices(),
                "status" => await StatusAsync(),
                "efx" => await ToggleEfxAsync(argList),
                "p48" or "48v" => await Toggle48Async(argList),
                "raw" => await RawStatusAsync(),
                "toggle48_selftest" => SelfTest(),
                _ => Usage(),
            };
        }
        catch (MidiException ex)
        {
            Console.Error.WriteLine($"[MIDI 错误] {ex.Message}");
            Console.Error.WriteLine("提示: 若端口被占用,请检查官方 Sonic Mix 或其它程序是否正在使用设备(不作断言)。");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[超时] 等待设备回包超时。");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[异常] {ex}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("用法: SonicCube.DeviceConsole <enum|status|efx on|off|p48 on|off|raw> [--raw]");
        return 1;
    }

    private static int EnumDevices()
    {
        Console.WriteLine("== MIDI 输入端口(含 \"Sonic Cube II\")==");
        var ins = MidiDevice.EnumerateInputs();
        foreach (var (i, name) in ins) Console.WriteLine($"  [{i}] {name}");
        if (ins.Count == 0) Console.WriteLine("  (无)");
        Console.WriteLine("== MIDI 输出端口 ==");
        var outs = MidiDevice.EnumerateOutputs();
        foreach (var (i, name) in outs) Console.WriteLine($"  [{i}] {name}");
        if (outs.Count == 0) Console.WriteLine("  (无)");
        return ins.Count > 0 && outs.Count > 0 ? 0 : 4;
    }

    private static void Hook(DeviceSession s)
    {
        if (_raw)
            s.RawFrame += (tx, frame) =>
                Console.WriteLine($"  {(tx ? "TX" : "RX")} {Convert.ToHexString(frame)}");
        s.MetersReceived += m =>
            Console.WriteLine($"  [电平] {string.Join(" ", m.Select(v => v.ToString("0.0")))}");
    }

    private static async Task<int> StatusAsync()
    {
        Console.WriteLine("打开设备并握手...");
        using var s = DeviceSession.OpenFirst();
        Hook(s);
        Console.WriteLine($"  输入: {s.Device.InName}\n  输出: {s.Device.OutName}");
        await s.HandshakeAsync();
        Console.WriteLine("握手完成。\n");
        PrintGlobal(s.Global);
        PrintPreset(s.Preset);
        return 0;
    }

    private static async Task<int> RawStatusAsync()
    {
        _raw = true;
        return await StatusAsync();
    }

    private static async Task<int> ToggleEfxAsync(List<string> args)
    {
        if (args.Count < 2 || (args[1] != "on" && args[1] != "off")) return Usage();
        int target = args[1] == "on" ? 1 : 0;
        using var s = DeviceSession.OpenFirst();
        Hook(s);
        await s.HandshakeAsync();
        Console.WriteLine($"当前 EFX = {s.Global.Efx},目标 = {target}");
        var resp = await s.SetIntAsync(CommandAddress.Efx, target);
        Console.WriteLine($"EFX 设置回包: {Convert.ToHexString(resp)} → 缓存 EFX = {s.Global.Efx}");
        return s.Global.Efx == target ? 0 : 5;
    }

    private static async Task<int> Toggle48Async(List<string> args)
    {
        if (args.Count < 2 || (args[1] != "on" && args[1] != "off")) return Usage();
        Console.WriteLine("警告: 48V 幻象电源可能损坏未正确连接的话筒/设备!");
        Console.Write("确认你已了解风险并授权操作 48V? 输入 YES 继续: ");
        var line = Console.ReadLine();
        if (!string.Equals(line, "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("已取消,未授权 48V。");
            return 6;
        }
        int target = args[1] == "on" ? 1 : 0;
        using var s = DeviceSession.OpenFirst();
        Hook(s);
        await s.HandshakeAsync();
        Console.WriteLine($"当前 48V = {s.Global.Phantom48V},目标 = {target}");
        var resp = await s.SetIntAsync(CommandAddress.Phantom48V, target);
        Console.WriteLine($"48V 设置回包: {Convert.ToHexString(resp)} → 缓存 48V = {s.Global.Phantom48V}");
        return s.Global.Phantom48V == target ? 0 : 5;
    }

    // 无设备时的自检:验证编解码器往返一致(不触碰硬件)。
    private static int SelfTest()
    {
        var frames = SonicCube.Protocol.Frames.SysExFrame.Encode(0x00020011, SonicCube.Protocol.Frames.FrameFlag.Set, Payload.Int32(1));
        var parser = new SonicCube.Protocol.Frames.SysExFrame.Parser();
        var done = new List<(uint, byte[])>();
        foreach (var f in frames) parser.Feed(f, done);
        bool ok = done.Count == 1 && done[0].Item1 == 0x00020011 && Payload.ReadInt32(done[0].Item2) == 1;
        Console.WriteLine(ok ? "自检通过(编解码往返一致)" : "自检失败");
        return ok ? 0 : 7;
    }

    private static void PrintGlobal(GlobalState g)
    {
        Console.WriteLine("== 全局状态 (0x00000011) ==");
        Console.WriteLine($"  hiEn={g.HiEn} 48V={g.Phantom48V} EFX={g.Efx} RVB={g.ReverbOn} MONITOR={g.Monitor} ONAIR={g.OnAir} 总静音={g.MasterMute} autoOutput={g.AutoOutput}");
    }

    private static void PrintPreset(PresetState p)
    {
        Console.WriteLine("== 预设状态 (0x00000010) ==");
        Console.WriteLine($"  inverseSw=[{string.Join(",", p.InverseSw)}] hpfSw=[{string.Join(",", p.HpfSw)}] lowCut=[{string.Join(",", p.LowCut)}] loopback=[{string.Join(",", p.LoopbackSw)}]");
        Console.WriteLine($"  dspRecSw={p.DspRecSw} fxModuleEn=0b{Convert.ToString(p.FxModuleEn, 2).PadLeft(6, '0')} mono={p.MonoSw}");
        Console.WriteLine($"  fxAlg=[{string.Join(", ", p.FxAlg.Select(a => $"0x{a:X8}"))}]");
        Console.WriteLine($"  muteSw=[{string.Join(",", p.MuteSw)}] soloSw=[{string.Join(",", p.SoloSw)}]");
        Console.WriteLine($"  volPot(dB)=[{string.Join(", ", p.VolPot.Select(v => v.ToString("0.0")))}]");
        Console.WriteLine($"  phase=[{string.Join(", ", p.Phase.Select(v => v.ToString("0.0")))}] send=[{string.Join(", ", p.Send.Select(v => v.ToString("0.00")))}]");
    }
}
