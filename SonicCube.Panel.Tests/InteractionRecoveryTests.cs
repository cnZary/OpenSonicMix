using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicCube.Panel.Ipc;
using SonicCube.Panel.Localization;
using SonicCube.Panel.Views;
using SonicCube.Panel.Controls;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Tests;

public sealed partial class SimulationTests
{
    private static void AssertInteractionRecovery()
    {
        var original = Loc.Culture;
        try
        {
            Loc.Apply(Loc.SystemUiCulture.Name == "en-US" ? "zh-CN" : "en-US");
            Loc.Apply("auto");
            Assert.AreEqual(Loc.SystemUiCulture.Name, Loc.Culture.Name);

            var algorithm = new AlgorithmEditorView(FxCatalog.ModuleComp);
            algorithm.SetMask(true);
            Assert.IsFalse(((ComboBox)algorithm.FindName("AlgSelect")).IsEnabled);
            Assert.IsFalse(((Grid)algorithm.FindName("KnobHost")).IsEnabled);
            algorithm.SetMask(false);
            Assert.IsTrue(((ComboBox)algorithm.FindName("AlgSelect")).IsEnabled);
            var reverb = new ReverbView();
            reverb.SetMask(true);
            Assert.IsFalse(((ComboBox)reverb.FindName("AlgSelect")).IsEnabled);
            var eq = new GlobalEqEditorView();
            eq.SetMask(true);
            Assert.IsFalse(((UIElement)eq.FindName("Eq")).IsEnabled);
            Assert.IsFalse(((UIElement)eq.FindName("Vol")).IsEnabled);

            using var client = new TrayClient("unused-" + Guid.NewGuid().ToString("N"));
            var remote = new MainWindow(client, false);
            InvokePrivate(remote, "ApplySnapshot", new DeviceStateDto { Connected = true });
            var mic = Field<InputChannelView>(remote, "Mic1");
            var bottom = (BottomBarView)remote.FindName("BottomBar");
            Assert.IsTrue(mic.IsEnabled);
            InvokePrivate(remote, "OnDeviceConnectionChanged", false);
            Assert.IsFalse(mic.IsEnabled);
            Assert.IsFalse(((ToggleButton)bottom.FindName("BtnEfx")).IsEnabled);
            Assert.IsTrue(((Button)bottom.FindName("MenuBtn")).IsEnabled);
            InvokePrivate(remote, "ApplySnapshot", new DeviceStateDto { Connected = true });
            Assert.IsTrue(mic.IsEnabled);

            AssertFailedWriteRouting(remote, mic);

            typeof(MainWindow).GetField("_closing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(remote, true);

            var settings = new SettingsWindow(null, new SettingsDto { Language = "en-US", AlwaysOnTop = false });
            try
            {
                Loc.Apply("en-US");
                int attempts = 0;
                SettingsDto? saved = null;
                settings.SaveSettings = dto =>
                {
                    attempts++;
                    return Task.FromResult<IpcMessage?>(new IpcMessage { Success = attempts > 1, ErrorCode = "timeout" });
                };
                settings.SettingsSaved += dto => saved = dto;
                ((ComboBox)settings.FindName("Lang")).SelectedIndex = 2;
                Assert.AreEqual("en-US", Loc.Culture.Name, "Failed language save must restore the confirmed language.");
                Assert.AreEqual(1, ((ComboBox)settings.FindName("Lang")).SelectedIndex);
                var retry = (Button)settings.FindName("RetryBtn");
                Assert.AreEqual(Visibility.Visible, retry.Visibility);
                Assert.IsFalse(string.IsNullOrWhiteSpace(((TextBlock)settings.FindName("SaveStatus")).Text));
                retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual(2, attempts);
                Assert.AreEqual("zh-CN", saved?.Language);
                Assert.AreEqual("zh-CN", Loc.Culture.Name);
                Assert.AreEqual(Visibility.Collapsed, retry.Visibility);
            }
            finally { settings.Close(); }
        }
        finally { Loc.SetCulture(original); }
    }

    private static void AssertFailedWriteRouting(MainWindow remote, InputChannelView mic)
    {
        var state = new DeviceStateDto { Connected = true, MonoSw = 1, ReverbOn = 1, FxModuleEn = 31 };
        foreach (var module in FxCatalog.Modules)
        {
            var alg = module.Algorithms[0];
            state.FxAlg[module.ModuleId] = alg.FxId;
            foreach (var param in alg.Params)
                state.FxParaVal[module.ModuleId * PresetState.ParamsPerModule + param.AlgId] = param.Default;
        }
        state.Send[0] = 0.1f;
        state.Phase[0] = 10;
        InvokePrivate(remote, "ApplySnapshot", state);

        var send = (KnobControl)mic.FindName("SendKnob");
        send.Value = 29; // This round trip exercises binary float conversion at the view boundary.
        InvokePrivate(send, "SuppressEchoBriefly");
        InvokePrivate(remote, "SendFloat", CommandAddress.WithLocation(CommandAddress.Send, 0), ValueMapping.SendToProtocol(29));
        Assert.AreEqual(ValueMapping.SendToUi(0.1f), send.Value);
        var pan = (PanSliderControl)mic.FindName("Pan");
        pan.Value = 20;
        InvokePrivate(pan, "SuppressEchoBriefly");
        InvokePrivate(remote, "SendFloat", CommandAddress.WithLocation(CommandAddress.Phase, 0), 20f);
        Assert.AreEqual(10, pan.Value);

        var effects = (EffectSectionView)remote.FindName("Fx");
        effects.ShowEffect(0);
        var comp = Field<AlgorithmEditorView>(effects, "_comp");
        var compKnob = (KnobControl)((Grid)comp.FindName("KnobHost")).Children[0];
        compKnob.Value = 70;
        InvokePrivate(compKnob, "SuppressEchoBriefly");
        InvokePrivate(remote, "SendFxParam", FxCatalog.ModuleComp, 0, 70f);
        Assert.AreEqual(20, compKnob.Value);

        effects.ShowEffect(1);
        var graph = Field<GlobalEqEditorView>(effects, "_graphEq");
        var volume = (LinearFaderControl)graph.FindName("Vol");
        volume.Value = 20;
        InvokePrivate(volume, "SuppressEchoBriefly");
        InvokePrivate(remote, "SendFxParam", FxCatalog.ModuleGraphEq, 7, 70f);
        Assert.AreEqual(0, volume.Value);
        Assert.AreEqual("0", ((TextBlock)graph.FindName("VolValue")).Text);

        var reverb = (ReverbView)remote.FindName("Rvb");
        var rvbKnob = ((Grid)reverb.FindName("KnobHost")).Children.OfType<KnobControl>().First();
        var definition = FxCatalog.GetModule(FxCatalog.ModuleRvb)!.Algorithms[0].Params[0];
        rvbKnob.Value = definition.Max;
        InvokePrivate(rvbKnob, "SuppressEchoBriefly");
        InvokePrivate(remote, "SendFxParam", FxCatalog.ModuleRvb, definition.AlgId, definition.Max);
        Assert.AreEqual((double)definition.Default, rvbKnob.Value);
    }
}
