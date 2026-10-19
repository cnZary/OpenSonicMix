using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using SonicCube.Panel.Controls;
using SonicCube.Panel.Localization;
using SonicCube.Panel.Views;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Tests;

public sealed partial class SimulationTests
{
    // Run with the existing window test so Application and language callbacks stay on one STA.
    private static void AssertLocalSettingsPreview(MainWindow owner)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalLocalization = Loc.Culture;
        bool originalTopmost = owner.Topmost;
        Loc.Apply("en-US");
        var original = new SettingsDto { Language = "en-US", AlwaysOnTop = false, StartWithWindows = false };
        var settings = new SettingsWindow(null, original);
        SettingsDto? saved = null;
        settings.SettingsSaved += value =>
        {
            saved = value;
            InvokePrivate(owner, "ApplySettings", value);
        };
        try
        {
            ((CheckBox)settings.FindName("AlwaysOnTop")!).IsChecked = true;
            Assert.IsNotNull(saved);
            Assert.IsTrue(saved.AlwaysOnTop);
            Assert.IsTrue(owner.Topmost);

            ((ComboBox)settings.FindName("Lang")!).SelectedIndex = 2;
            Assert.AreEqual("zh-CN", saved.Language);
            Assert.AreEqual("zh-CN", Loc.Culture.Name);
            Assert.AreEqual(Loc.Get("Settings"), settings.Title);
            Assert.AreEqual(Loc.Get("AppTitle"), owner.Title);

            ((RadioButton)settings.FindName("ModeLow")!).IsChecked = true;
            Assert.AreEqual("lowResource", saved.WorkMode);
            Assert.IsFalse(((CheckBox)settings.FindName("Reapply")!).IsEnabled);
            ((CheckBox)settings.FindName("StartWithWindows")!).IsChecked = true;
            ((CheckBox)settings.FindName("Enable48V")!).IsChecked = true;
            Assert.IsTrue(saved.StartWithWindows);
            Assert.IsTrue(saved.Enable48V);
            Assert.IsFalse(saved.PhantomPowerConsent);

            var openLog = (Button)settings.FindName("OpenLog")!;
            Assert.IsFalse(openLog.IsEnabled);
            Assert.AreEqual(Loc.Get("Simulated"), openLog.ToolTip);
            Assert.IsTrue(ToolTipService.GetShowOnDisabled(openLog));
            Assert.IsFalse(original.AlwaysOnTop, "The supplied settings object must remain unchanged.");
            Assert.IsFalse(original.StartWithWindows);

            var reopened = new SettingsWindow(null, saved);
            try
            {
                Assert.AreEqual(2, ((ComboBox)reopened.FindName("Lang")!).SelectedIndex);
                Assert.IsTrue(((CheckBox)reopened.FindName("AlwaysOnTop")!).IsChecked);
                Assert.IsTrue(((CheckBox)reopened.FindName("StartWithWindows")!).IsChecked);
                Assert.IsTrue(((RadioButton)reopened.FindName("ModeLow")!).IsChecked);
            }
            finally { reopened.Close(); }
        }
        finally
        {
            settings.Close();
            owner.Topmost = originalTopmost;
            Loc.SetCulture(originalLocalization);
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void AssertResetDuringEchoSuppression(MainWindow window)
    {
        var mic = Field<InputChannelView>(window, "Mic1");
        var send = (KnobControl)mic.FindName("SendKnob")!;
        send.Value = 90;
        InvokePrivate(send, "SuppressEchoBriefly");
        InvokePrivate(window, "SendFloat", CommandAddress.WithLocation(CommandAddress.Send, 0), 0.9f);
        Assert.AreEqual(0.9f, Field<PresetState>(window, "_preset").Send[0]);
        InvokePrivate(window, "OnFullResetRequested");
        Assert.AreEqual(0.25f, Field<PresetState>(window, "_preset").Send[0]);
        Assert.AreEqual(25.0, send.Value, "A local reset must immediately replace the last committed value.");

        var remote = new KnobControl();
        remote.Configure(0, 100, 1, 0, "SEND");
        remote.Value = 90;
        InvokePrivate(remote, "SuppressEchoBriefly");
        remote.SetValueSilently(25);
        Assert.AreEqual(90.0, remote.Value, "Remote controls must retain their echo protection.");
    }

    private static void InvokePrivate(object owner, string method, params object[] arguments) =>
        owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
}
