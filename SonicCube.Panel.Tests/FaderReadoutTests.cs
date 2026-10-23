using System.Windows;
using System.Windows.Controls;
using SonicCube.Panel.Controls;
using SonicCube.Panel.Views;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Tests;

public sealed partial class SimulationTests
{
    // Called by the existing Application-owning STA window test.
    private static void AssertFaderReadoutConsistency(MainWindow window)
    {
        var preset = new PresetState();
        Array.Fill(preset.VolPot, -12f);
        foreach (string name in new[] { "Mic1", "Line2", "Usb", "Otg" })
        {
            var view = Field<FrameworkElement>(window, name);
            var fader = (VolumeFaderControl)view.FindName("Fader");
            var readout = (TextBlock)view.FindName("FaderValue");
            ControlInteractionTests.SetField(fader, "_dragging", true);
            fader.Db = -3;
            if (view is InputChannelView input) input.ApplyState(preset, new GlobalState());
            else ((PlaybackChannelView)view).ApplyState(preset);
            Assert.AreEqual(-3, fader.Db);
            Assert.AreEqual(ValueMapping.FaderText(fader.Db), readout.Text, name);
            fader.ResetSynchronization();
            Assert.AreEqual(-12, fader.Db);
            Assert.AreEqual(ValueMapping.FaderText(fader.Db), readout.Text, name);
        }
    }
}
