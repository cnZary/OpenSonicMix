namespace SonicCube.Protocol.Models;

/// <summary>单个算法参数定义(min/max/step 为权威范围,valueRange 仅 UI 文案)。</summary>
public readonly record struct FxParamDef(int AlgId, string Name, float Default, float Min, float Max, float Step, int WidgetType);

/// <summary>一个效果算法(fx)。FxId 高字节=类别码,低 16 位=全局算法编号。</summary>
public sealed record FxAlgorithmDef(int FxId, string DisplayName, string Title, string Type, string Origin, FxParamDef[] Params);

/// <summary>一个效果模块槽位。</summary>
public sealed record FxModuleDef(int ModuleId, string Name, FxAlgorithmDef[] Algorithms);

/// <summary>
/// 效果算法表(见 re/modules_report.md):5 模块 / 15 算法 / 70 参数。
/// 模块 0=COMP, 1=Global EQ(GRAPHEQ), 2=INST, 3=EQ, 4=RVB。无 CAB(moduleId 5)定义。
/// </summary>
public static class FxCatalog
{
    public const int ModuleComp = 0;
    public const int ModuleGraphEq = 1;  // UI 中 MIC1 的 EQ,叫 "Global EQ"
    public const int ModuleInst = 2;
    public const int ModuleEq = 3;
    public const int ModuleRvb = 4;

    public static IReadOnlyList<FxModuleDef> Modules { get; } = Build();

    /// <summary>按模块号取定义,越界返回 null。</summary>
    public static FxModuleDef? GetModule(int moduleId) =>
        (uint)moduleId < (uint)Modules.Count ? Modules[moduleId] : null;

    /// <summary>在指定模块内按 fxId 找算法,未找到返回 null。</summary>
    public static FxAlgorithmDef? FindAlgorithm(int moduleId, int fxId)
    {
        var m = GetModule(moduleId);
        if (m is null) return null;
        foreach (var a in m.Algorithms)
            if (a.FxId == fxId) return a;
        return null;
    }

    private static FxParamDef P(int algId, string name, float def, float min, float max, float step, int widget = 0)
        => new(algId, name, def, min, max, step, widget);

    private static FxModuleDef[] Build() =>
    [
        // ── 模块 0 COMP ─────────────────────────────────────
        new FxModuleDef(ModuleComp, "COMP",
        [
            new FxAlgorithmDef(0, "COMP 1", "Comprosso", "Comp", "Ross Compressor",
            [
                P(0, "Sustain", 20, 0, 100, 1),
                P(1, "VOL", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(1, "COMP 2", "Comparoma 4", "Comp", "Keeley Compressor",
            [
                P(0, "Sustain", 20, 0, 100, 1),
                P(1, "Attack", 60, 0, 100, 1),
                P(2, "VOL", 50, 0, 100, 1),
                P(3, "Clip", 10, 0, 100, 1),
            ]),
        ]),

        // ── 模块 1 Global EQ(GRAPHEQ)─────────────────────────
        new FxModuleDef(ModuleGraphEq, "Global EQ",
        [
            new FxAlgorithmDef(0x0100004A, "Global EQ", "Global EQ", "Global EQ", "Global EQ on MP-100",
            [
                P(0, "LOW FREQ", 100, 20, 20000, 1),
                P(1, "LOW GAIN", 0, -20, 20, 1),
                P(2, "MID FREQ", 1000, 20, 20000, 1),
                P(3, "MID Q", 0.7f, 0.1f, 10.0f, 0.01f),
                P(4, "MID GAIN", 0, -20, 20, 1),
                P(5, "HIGH FREQ", 5000, 20, 20000, 1),
                P(6, "HIGH GAIN", 0, -20, 20, 1),
                P(7, "VOLUME", 50, 0, 100, 1),
            ]),
        ]),

        // ── 模块 2 INST ─────────────────────────────────────
        new FxModuleDef(ModuleInst, "INST",
        [
            new FxAlgorithmDef(0x07000001, "CLEAN", "Tweed Lux", "Clean", "Fender Tweed Deluxe 5E3",
            [
                P(0, "Gain", 30, 0, 100, 1),
                P(1, "Tone", 50, 0, 100, 1),
                P(2, "VOL", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x07000027, "OVERDRIVE", "VOKS 30TB", "Drive", "VOX AC30",
            [
                P(0, "Gain", 50, 0, 100, 1),
                P(1, "Tone Cut", 50, 0, 100, 1),
                P(2, "VOL", 50, 0, 100, 1),
                P(3, "Bass", 50, 0, 100, 1),
                P(4, "Treble", 50, 0, 100, 1),
                P(5, "Char", 0, 0, 1, 1, widget: 2), // Cool/Hot
            ]),
            new FxAlgorithmDef(0x07000047, "DISTORTION", "Soloist 100 Crunch", "Drive", "Soldano SLO100",
            [
                P(0, "Gain", 50, 0, 100, 1),
                P(1, "PRES", 50, 0, 100, 1),
                P(2, "VOL", 50, 0, 100, 1),
                P(3, "Bass", 50, 0, 100, 1),
                P(4, "Middle", 50, 0, 100, 1),
                P(5, "Treble", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x0700005D, "HI-GAIN", "Flyman B+", "Hi Gain", "ENGL Powerball CH4",
            [
                P(0, "Gain", 50, 0, 100, 1),
                P(1, "PRES", 50, 0, 100, 1),
                P(2, "VOL", 50, 0, 100, 1),
                P(3, "Bass", 50, 0, 100, 1),
                P(4, "Middle", 50, 0, 100, 1),
                P(5, "Treble", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x0800007A, "ACOUSTIC", "Acoustic Preamp 1", "Acoustic", "AER Colourizer (EQ f1)",
            [
                P(0, "Volume", 50, 0, 100, 1),
                P(1, "Tone", 50, 0, 100, 1),
                P(2, "Balance", 50, 0, 100, 1),
                P(3, "EQ Freq", 50, 0, 100, 1),
                P(4, "EQ Q", 50, 0, 100, 1),
                P(5, "EQ Gain", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x07000076, "BASS", "Tangerine Bass", "Bass", "Orange AD200B",
            [
                P(0, "Gain", 50, 0, 100, 1),
                P(1, "Master", 50, 0, 100, 1),
                P(2, "Bass", 50, 0, 100, 1),
                P(3, "Middle", 50, 0, 100, 1),
                P(4, "Treble", 50, 0, 100, 1),
            ]),
        ]),

        // ── 模块 3 EQ ───────────────────────────────────────
        new FxModuleDef(ModuleEq, "EQ",
        [
            new FxAlgorithmDef(0x01000035, "EQ 1", "Guitar EQ 1", "EQ", "Original",
            [
                P(0, "125Hz", 0, -50, 50, 1, widget: 3),
                P(1, "400Hz", 0, -50, 50, 1, widget: 3),
                P(2, "800Hz", 0, -50, 50, 1, widget: 3),
                P(3, "1.6kHz", 0, -50, 50, 1, widget: 3),
                P(4, "4kHz", 0, -50, 50, 1, widget: 3),
                P(5, "VOL", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x01000036, "EQ 2", "Guitar EQ 2", "EQ", "Original",
            [
                P(0, "100Hz", 0, -50, 50, 1, widget: 3),
                P(1, "500Hz", 0, -50, 50, 1, widget: 3),
                P(2, "1kHz", 0, -50, 50, 1, widget: 3),
                P(3, "3kHz", 0, -50, 50, 1, widget: 3),
                P(4, "6kHz", 0, -50, 50, 1, widget: 3),
                P(5, "VOL", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x0100003A, "EQ 3", "Bass EQ 2", "EQ", "Original",
            [
                P(0, "50Hz", 0, -50, 50, 1, widget: 3),
                P(1, "120Hz", 0, -50, 50, 1, widget: 3),
                P(2, "400Hz", 0, -50, 50, 1, widget: 3),
                P(3, "800Hz", 0, -50, 50, 1, widget: 3),
                P(4, "4.5kHz", 0, -50, 50, 1, widget: 3),
                P(5, "VOL", 50, 0, 100, 1),
            ]),
        ]),

        // ── 模块 4 RVB ──────────────────────────────────────
        new FxModuleDef(ModuleRvb, "RVB",
        [
            new FxAlgorithmDef(0x0C000000, "Room", "Room", "Reverb", "",
            [
                P(0, "Mix", 30, 0, 100, 1),
                P(2, "Decay", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x0C000001, "Hall", "Hall", "Reverb", "",
            [
                P(0, "Mix", 30, 0, 100, 1),
                P(2, "Decay", 50, 0, 100, 1),
            ]),
            new FxAlgorithmDef(0x0C000010, "Plate", "Large Plate", "Reverb", "",
            [
                P(0, "Mix", 30, 0, 100, 1),
                P(2, "Decay", 50, 0, 100, 1),
            ]),
        ]),
    ];
}
