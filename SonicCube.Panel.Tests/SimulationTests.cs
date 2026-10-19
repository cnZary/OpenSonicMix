using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicCube.Panel.Simulation;
using SonicCube.Panel.Views;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Tests;

[STATestClass]
public sealed partial class SimulationTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void HpfAndLowCut_ChangeOnlyTheSelectedChannel(int channel)
    {
        var device = new SimulatedDevice();
        device.SetInt(Address(CommandAddress.HpfSwitch, channel), 1);
        device.SetInt(Address(CommandAddress.LowCut, channel), 1);
        Assert.AreEqual(1, device.Preset.HpfSw[channel]);
        Assert.AreEqual(1, device.Preset.LowCut[channel]);
        Assert.AreEqual(0, device.Preset.HpfSw[1 - channel]);
        Assert.AreEqual(0, device.Preset.LowCut[1 - channel]);
        device.SetInt(Address(CommandAddress.HpfSwitch, channel), 0);
        Assert.AreEqual(0, device.Preset.HpfSw[channel]);
        Assert.AreEqual(1, device.Preset.LowCut[channel]);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void FxEnable_PreservesEveryOtherModule(int module)
    {
        var device = new SimulatedDevice();
        int original = device.Preset.FxModuleEn;
        device.SetInt(Address(CommandAddress.FxModuleEnable, module), 0);
        Assert.AreEqual(original & ~(1 << module), device.Preset.FxModuleEn);
        device.SetInt(Address(CommandAddress.FxModuleEnable, module), 1);
        Assert.AreEqual(original | (1 << module), device.Preset.FxModuleEn);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void MuteAndSolo_AreMutuallyExclusive(int channel)
    {
        var device = new SimulatedDevice();
        device.SetInt(Address(CommandAddress.MuteSwitch, channel), 1);
        device.SetInt(Address(CommandAddress.SoloSwitch, channel), 1);
        Assert.AreEqual(0, device.Preset.MuteSw[channel]);
        Assert.AreEqual(1, device.Preset.SoloSw[channel]);
        device.SetInt(Address(CommandAddress.MuteSwitch, channel), 1);
        Assert.AreEqual(1, device.Preset.MuteSw[channel]);
        Assert.AreEqual(0, device.Preset.SoloSw[channel]);
    }

    [TestMethod]
    public void MonoPhaseAndInverse_RetainTheirLocalSettings()
    {
        var device = new SimulatedDevice();
        device.Toggle("Mono");
        device.SetFloat(Address(CommandAddress.Phase, 1), 32);
        device.SetInt(Address(CommandAddress.Inverse, 1), 1);
        Assert.AreEqual(1, device.Preset.MonoSw);
        Assert.AreEqual(32f, device.Preset.Phase[1]);
        Assert.AreEqual(1, device.Preset.InverseSw[1]);
        Assert.AreEqual(0, device.Preset.InverseSw[0]);
        device.Toggle("mono");
        Assert.AreEqual(0, device.Preset.MonoSw);
    }

    [TestMethod]
    [DataRow("Phantom48V")]
    [DataRow("Efx")]
    [DataRow("Monitor")]
    [DataRow("OnAir")]
    [DataRow("MasterMute")]
    [DataRow("ReverbOn")]
    public void GlobalToggles_CanBeSwitchedOnAndOff(string name)
    {
        var device = new SimulatedDevice();
        var property = typeof(GlobalState).GetProperty(name)!;
        int initial = (int)property.GetValue(device.Global)!;
        device.Toggle(name);
        Assert.AreEqual(1 - initial, property.GetValue(device.Global));
        device.Toggle(name);
        Assert.AreEqual(initial, property.GetValue(device.Global));
    }

    [TestMethod]
    public void InitialAlgorithms_HaveTheirOwnCatalogDefaults()
    {
        var device = new SimulatedDevice();
        foreach (var module in FxCatalog.Modules)
        {
            var algorithm = FxCatalog.FindAlgorithm(module.ModuleId, device.Preset.FxAlg[module.ModuleId]);
            Assert.IsNotNull(algorithm, $"Module {module.ModuleId} must select a known algorithm.");
            AssertDefaults(device, module.ModuleId, algorithm);
        }
    }

    [TestMethod]
    [DataRow(FxCatalog.ModuleComp)]
    [DataRow(FxCatalog.ModuleInst)]
    [DataRow(FxCatalog.ModuleEq)]
    [DataRow(FxCatalog.ModuleRvb)]
    public void ChoosingAnAlgorithm_ResetsOnlyItsParameters(int module)
    {
        var device = new SimulatedDevice();
        var selected = FxCatalog.GetModule(module)!.Algorithms.First(a => a.FxId != device.Preset.FxAlg[module]);
        int other = module == FxCatalog.ModuleComp ? FxCatalog.ModuleInst : FxCatalog.ModuleComp;
        var otherParameters = Enumerable.Range(0, PresetState.ParamsPerModule).Select(p => device.Preset.GetFxPara(other, p)).ToArray();
        device.SetInt(Address(CommandAddress.FxAlgorithm, module), selected.FxId);
        Assert.AreEqual(selected.FxId, device.Preset.FxAlg[module]);
        AssertDefaults(device, module, selected);
        CollectionAssert.AreEqual(otherParameters, Enumerable.Range(0, PresetState.ParamsPerModule).Select(p => device.Preset.GetFxPara(other, p)).ToArray());
    }

    [TestMethod]
    public void FxParameters_UpdateAndRespectTheSelectedAlgorithmsRange()
    {
        var device = new SimulatedDevice();
        const int module = FxCatalog.ModuleComp;
        var parameter = FxCatalog.FindAlgorithm(module, device.Preset.FxAlg[module])!.Params[0];
        device.SetFxParameter(module, parameter.AlgId, parameter.Max);
        Assert.AreEqual(parameter.Max, device.Preset.GetFxPara(module, parameter.AlgId));
        device.SetFxParameter(module, parameter.AlgId, parameter.Min - 100);
        Assert.AreEqual(parameter.Min, device.Preset.GetFxPara(module, parameter.AlgId));
        device.SetFxParameter(module, parameter.AlgId, parameter.Max + 100);
        Assert.AreEqual(parameter.Max, device.Preset.GetFxPara(module, parameter.AlgId));
    }

    [TestMethod]
    public void ResetModule_DisablesItAndRestoresItsCurrentAlgorithmDefaults()
    {
        var device = new SimulatedDevice();
        var algorithm = FxCatalog.FindAlgorithm(FxCatalog.ModuleComp, device.Preset.FxAlg[FxCatalog.ModuleComp])!;
        device.SetFxParameter(FxCatalog.ModuleComp, algorithm.Params[0].AlgId, algorithm.Params[0].Max);
        device.SetInt(Address(CommandAddress.HpfSwitch, 1), 1);
        int enabledBefore = device.Preset.FxModuleEn;
        device.ResetModule(FxCatalog.ModuleComp);
        Assert.AreEqual(enabledBefore & ~(1 << FxCatalog.ModuleComp), device.Preset.FxModuleEn);
        Assert.AreEqual(algorithm.FxId, device.Preset.FxAlg[FxCatalog.ModuleComp]);
        AssertDefaults(device, FxCatalog.ModuleComp, algorithm);
        Assert.AreEqual(1, device.Preset.HpfSw[1]);
    }

    [TestMethod]
    public void ResetAll_RestoresTheDemoAndItsMeters()
    {
        var device = new SimulatedDevice();
        var defaults = new SimulatedDevice();
        device.SetInt(Address(CommandAddress.HpfSwitch, 0), 1);
        device.SetInt(Address(CommandAddress.LowCut, 0), 1);
        device.SetInt(Address(CommandAddress.MuteSwitch, 2), 1);
        device.SetFloat(Address(CommandAddress.VolumePot, 0), -40);
        device.Toggle("MasterMute");
        device.ResetModule(FxCatalog.ModuleInst);
        device.ResetAll();
        CollectionAssert.AreEqual(defaults.Preset.HpfSw, device.Preset.HpfSw);
        CollectionAssert.AreEqual(defaults.Preset.LowCut, device.Preset.LowCut);
        CollectionAssert.AreEqual(defaults.Preset.MuteSw, device.Preset.MuteSw);
        CollectionAssert.AreEqual(defaults.Preset.VolPot, device.Preset.VolPot);
        CollectionAssert.AreEqual(defaults.Preset.FxAlg, device.Preset.FxAlg);
        CollectionAssert.AreEqual(defaults.Preset.FxParaVal, device.Preset.FxParaVal);
        Assert.AreEqual(defaults.Preset.FxModuleEn, device.Preset.FxModuleEn);
        Assert.AreEqual(defaults.Global.MasterMute, device.Global.MasterMute);
        CollectionAssert.AreEqual(defaults.GetMeters(TimeSpan.FromSeconds(1)), device.GetMeters(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public void Meters_VaryByChannelAndReachZeroDbAtDifferentTimes()
    {
        var device = new SimulatedDevice();
        var reachedZero = new bool[10];
        bool sawDifferentLevels = false, sawPartialPeak = false;
        for (int i = 0; i < 200; i++)
        {
            float[] meters = device.GetMeters(TimeSpan.FromMilliseconds(i * 50));
            Assert.HasCount(10, meters);
            Assert.IsTrue(meters.All(value => float.IsFinite(value) && value >= -100 && value <= 0));
            sawDifferentLevels |= meters.Distinct().Count() > 4;
            int peaks = 0;
            for (int channel = 0; channel < 10; channel++)
                if (Math.Abs(meters[channel]) < 0.0001) { reachedZero[channel] = true; peaks++; }
            sawPartialPeak |= peaks is > 0 and < 10;
            Assert.IsLessThan(10, peaks, "All meters must not peak together.");
        }
        Assert.IsTrue(sawDifferentLevels);
        Assert.IsTrue(sawPartialPeak);
        Assert.IsTrue(reachedZero.All(value => value), "Every channel should briefly reach 0 dB during the demo.");
    }

    [TestMethod]
    public void VolumeMuteSoloAndMasterMute_AffectGeneratedMeters()
    {
        var device = new SimulatedDevice();
        var time = TimeSpan.FromSeconds(0.2);
        float[] original = device.GetMeters(time);
        device.SetFloat(Address(CommandAddress.VolumePot, 0), device.Preset.VolPot[0] - 10);
        float[] quieter = device.GetMeters(time);
        Assert.AreEqual(original[0] - 10, quieter[0], 0.0001);
        Assert.AreEqual(original[1] - 10, quieter[1], 0.0001);
        Assert.AreEqual(original[4], quieter[4]);
        device.SetInt(Address(CommandAddress.MuteSwitch, 0), 1);
        CollectionAssert.AreEqual(new float[] { -100, -100 }, device.GetMeters(time).Take(2).ToArray());
        device.SetInt(Address(CommandAddress.SoloSwitch, 0), 1);
        float[] solo = device.GetMeters(time);
        Assert.IsGreaterThan(-100f, solo[0]);
        Assert.IsTrue(solo.Skip(2).Take(6).All(value => value == -100));
        device.Toggle("MasterMute");
        float[] masterMuted = device.GetMeters(time);
        CollectionAssert.AreEqual(solo.Take(8).ToArray(), masterMuted.Take(8).ToArray());
        CollectionAssert.AreEqual(new float[] { -100, -100 }, masterMuted.Skip(8).ToArray());
    }

    [TestMethod]
    public void SimulatedWindow_HpfButtonUpdatesItsBypassMaskWithoutIpc()
    {
        if (Application.Current is null)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
        }
        var window = new MainWindow(null, true);
        StopSimulationTimer(window);
        try
        {
            var effects = (EffectSectionView)window.FindName("Fx")!;
            var mic = Field<InputChannelView>(window, "Mic1");
            var button = (ToggleButton)mic.FindName("BtnHpf")!;
            effects.ShowEffect(2);
            var masks = Field<Border[]>(effects, "_hpfMasks");
            Assert.AreEqual(Visibility.Visible, masks[0].Visibility);
            button.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(1, Field<PresetState>(window, "_preset").HpfSw[0]);
            Assert.AreEqual(Visibility.Collapsed, masks[0].Visibility);
            button.IsChecked = false;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(0, Field<PresetState>(window, "_preset").HpfSw[0]);
            Assert.AreEqual(Visibility.Visible, masks[0].Visibility);
            AssertResetDuringEchoSuppression(window);
            AssertLocalSettingsPreview(window);
        }
        finally { StopSimulationTimer(window); }
    }

    private static uint Address(uint type, int location) => CommandAddress.WithLocation(type, location);

    private static void AssertDefaults(SimulatedDevice device, int module, FxAlgorithmDef algorithm)
    {
        foreach (var parameter in algorithm.Params)
            Assert.AreEqual(parameter.Default, device.Preset.GetFxPara(module, parameter.AlgId),
                $"{algorithm.DisplayName}: {parameter.Name} must use this algorithm's default.");
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;

    private static void StopSimulationTimer(MainWindow window)
    {
        object? timer = typeof(MainWindow).GetField("_simTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        timer?.GetType().GetMethod("Stop", Type.EmptyTypes)?.Invoke(timer, null);
        (timer as IDisposable)?.Dispose();
    }
}
