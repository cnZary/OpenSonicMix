using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Simulation;

/// <summary>本地演示设备:保留控制状态,并生成可重复的双声道演示电平。</summary>
public sealed class SimulatedDevice
{
    private static readonly float[] DefaultVolumes = [-8, -12, -5, -5];
    private static readonly double[] SignalFloors = [-29, -22, -35, -20, -27, -32, -38, -26, -23, -19];
    private static readonly double[] SignalRanges = [17, 12, 22, 10, 17, 19, 20, 12, 14, 10];
    private readonly object _sync = new();

    // 重置原地更新,窗口可一直持有这两个对象。
    public PresetState Preset { get; } = new();
    public GlobalState Global { get; } = new();

    public SimulatedDevice() => ResetAll();

    public void SetInt(uint address, int value)
    {
        lock (_sync)
        {
            uint type = CommandAddress.TypeOf(address);
            int location = CommandAddress.LocationOf(address);
            switch (type)
            {
                case CommandAddress.CommandReset:
                    ResetAll();
                    break;
                case CommandAddress.FxModuleEnable:
                    Preset.SetModuleEnabled(location, value != 0);
                    break;
                case CommandAddress.FxAlgorithm:
                    if (FxCatalog.FindAlgorithm(location, value) is not null)
                    {
                        Preset.FxAlg[location] = value;
                        ResetAlgorithmParameters(location);
                    }
                    break;
                case CommandAddress.Mono:
                    SetMono(value != 0);
                    break;
                default:
                    var data = Payload.Int32(value);
                    if ((type & 0xFF) == 0x10)
                        Preset.ApplyIndividual(type, location, data);
                    else if ((type & 0xFF) == 0x11)
                        Global.ApplyIndividual(type, value);
                    break;
            }
        }
    }

    public void SetFloat(uint address, float value)
    {
        if (!float.IsFinite(value)) return;
        lock (_sync)
        {
            int location = CommandAddress.LocationOf(address);
            switch (CommandAddress.TypeOf(address))
            {
                case CommandAddress.VolumePot when location < Preset.VolPot.Length:
                    Preset.VolPot[location] = Math.Clamp(value, -51, 10);
                    break;
                case CommandAddress.Phase when location < Preset.Phase.Length:
                    Preset.Phase[location] = Math.Clamp(value, -50, 50);
                    break;
                case CommandAddress.Send when location < Preset.Send.Length:
                    Preset.Send[location] = Math.Clamp(value, 0, 1);
                    break;
            }
        }
    }

    public void SetFxParameter(int module, int paramIndex, float value)
    {
        if ((uint)module >= PresetState.ModuleCount || (uint)paramIndex >= PresetState.ParamsPerModule || !float.IsFinite(value)) return;
        lock (_sync)
        {
            var algorithm = FxCatalog.FindAlgorithm(module, Preset.FxAlg[module]);
            if (algorithm is null) return;
            foreach (var parameter in algorithm.Params)
            {
                if (parameter.AlgId != paramIndex) continue;
                Preset.FxParaVal[module * PresetState.ParamsPerModule + paramIndex] = Math.Clamp(value, parameter.Min, parameter.Max);
                break;
            }
        }
    }

    public void Toggle(string name)
    {
        lock (_sync)
        {
            if (string.Equals(name, "Mono", StringComparison.OrdinalIgnoreCase))
            {
                SetMono(Preset.MonoSw != 1);
                return;
            }
            switch (name?.ToUpperInvariant())
            {
                case "PHANTOM48V": Global.Phantom48V = Flip(Global.Phantom48V); break;
                case "EFX": Global.Efx = Flip(Global.Efx); break;
                case "MONITOR": Global.Monitor = Flip(Global.Monitor); break;
                case "ONAIR": Global.OnAir = Flip(Global.OnAir); break;
                case "MASTERMUTE": Global.MasterMute = Flip(Global.MasterMute); break;
                case "REVERBON": Global.ReverbOn = Flip(Global.ReverbOn); break;
            }
        }
    }

    public void ResetModule(int module)
    {
        if ((uint)module >= PresetState.ModuleCount) return;
        lock (_sync)
        {
            Preset.SetModuleEnabled(module, false);
            ResetAlgorithmParameters(module);
        }
    }

    public void ResetAll()
    {
        lock (_sync)
        {
            Array.Clear(Preset.InverseSw);
            Array.Clear(Preset.HpfSw);
            Array.Clear(Preset.LowCut);
            Array.Clear(Preset.LoopbackSw);
            Array.Clear(Preset.MuteSw);
            Array.Clear(Preset.SoloSw);
            Array.Clear(Preset.FxAlg);
            Array.Clear(Preset.FxParaVal);
            Preset.DspRecSw = 1;
            Preset.FxModuleEn = 0b011111;
            for (int module = 0; module < PresetState.ModuleCount; module++)
            {
                Preset.FxSlot[module] = module;
                var definition = FxCatalog.GetModule(module);
                if (definition is null || definition.Algorithms.Length == 0) continue;
                Preset.FxAlg[module] = module switch
                {
                    FxCatalog.ModuleComp => 1,
                    FxCatalog.ModuleInst => 0x07000027,
                    _ => definition.Algorithms[0].FxId,
                };
                ResetAlgorithmParameters(module);
            }
            DefaultVolumes.CopyTo(Preset.VolPot, 0);
            Preset.Send[0] = 0.25f;
            Preset.Send[1] = 0.1f;
            SetMono(false);
            Global.HiEn = 0;
            Global.Phantom48V = 0;
            Global.Efx = 1;
            Global.ReverbOn = 1;
            Global.Monitor = 1;
            Global.OnAir = 1;
            Global.MasterMute = 0;
            Global.AutoOutput = 0;
        }
    }

    /// <summary>
    /// 返回 MIC1、INST2、USB、OTG、MASTER 各 L/R 共 10 路 dB。
    /// 每 8 秒依次触顶,平台 450ms;推子按相对演示默认值改变增益,静音/独听作用于源和总输出。
    /// </summary>
    public float[] GetMeters(TimeSpan elapsed)
    {
        var meters = new float[10];
        double seconds = Math.Max(0, elapsed.TotalSeconds);
        lock (_sync)
        {
            Span<double> referencePower = stackalloc double[2];
            Span<double> mixedPower = stackalloc double[2];
            referencePower.Clear();
            mixedPower.Clear();
            bool anySolo = Preset.SoloSw[0] == 1 || Preset.SoloSw[1] == 1;
            for (int i = 0; i < 8; i++)
            {
                int channel = i / 2, side = i % 2;
                float sourceDb = SignalDb(seconds, i);
                referencePower[side] += Math.Pow(10, sourceDb / 10.0);
                bool audible = Preset.MuteSw[channel] != 1
                    && (!anySolo || (channel < 2 && Preset.SoloSw[channel] == 1))
                    && Preset.VolPot[channel] > -50.18f;
                meters[i] = audible
                    ? Math.Clamp(sourceDb + (Preset.VolPot[channel] - DefaultVolumes[channel]), -100f, 0f)
                    : -100f;
                if (audible) mixedPower[side] += Math.Pow(10, meters[i] / 10.0);
            }
            for (int side = 0; side < 2; side++)
            {
                if (Global.MasterMute == 1 || mixedPower[side] <= 0)
                    meters[8 + side] = -100;
                else
                {
                    // 独立主输出节奏用于看清左右差异,源静音/增益则按总能量比例反映。
                    double gainDb = 10 * Math.Log10(mixedPower[side] / referencePower[side]);
                    meters[8 + side] = (float)Math.Clamp(SignalDb(seconds, 8 + side) + gainDb, -100, 0);
                }
            }
        }
        return meters;
    }

    private void ResetAlgorithmParameters(int module)
    {
        int start = module * PresetState.ParamsPerModule;
        Array.Clear(Preset.FxParaVal, start, PresetState.ParamsPerModule);
        var algorithm = FxCatalog.FindAlgorithm(module, Preset.FxAlg[module]);
        if (algorithm is null) return;
        foreach (var parameter in algorithm.Params)
            Preset.FxParaVal[start + parameter.AlgId] = parameter.Default;
    }

    private void SetMono(bool enabled)
    {
        Preset.MonoSw = enabled ? 1 : 0;
        Preset.Phase[0] = enabled ? 0 : -50;
        Preset.Phase[1] = enabled ? 0 : 50;
    }

    private static int Flip(int value) => value == 1 ? 0 : 1;

    private static float SignalDb(double seconds, int channel)
    {
        double pulse = 0.5 + 0.5 * Math.Sin(seconds * (1.7 + channel * 0.31) + channel * 1.13);
        double ripple = 0.5 + 0.5 * Math.Sin(seconds * (4.3 + channel * 0.47) + channel * 0.71);
        double normal = SignalFloors[channel] + SignalRanges[channel] * (0.8 * pulse + 0.2 * ripple);
        double phase = ((seconds - 0.5 - channel * 0.7) % 8 + 8) % 8;
        double burst = phase switch
        {
            < 0.12 => phase / 0.12,
            < 0.57 => 1,
            < 0.82 => 1 - (phase - 0.57) / 0.25,
            _ => 0,
        };
        return (float)(normal * (1 - burst));
    }
}
