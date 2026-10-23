using System.Windows;
using System.Windows.Media;
using SonicCube.Panel.Controls;
using static SonicCube.Panel.Tests.ControlInteractionTests;

namespace SonicCube.Panel.Tests;

[STATestClass]
public sealed class GraphicEqTests
{
    [TestMethod]
    public void Curve_CachesUnchangedStateAndHoverButRebuildsForParameterOrSize()
    {
        var eq = new GraphicEqControl();
        Invoke(eq, "EnsureCurveCache", 320.0, 180.0);
        var original = Field<StreamGeometry>(eq, "_strokeGeometry");
        eq.SetPoint(0, 100, 0);
        eq.SetQ(0.7);
        SetField(eq, "_hoverIndex", 1);
        Invoke(eq, "EnsureCurveCache", 320.0, 180.0);
        Assert.AreSame(original, Field<StreamGeometry>(eq, "_strokeGeometry"));
        eq.SetQ(2);
        Invoke(eq, "EnsureCurveCache", 320.0, 180.0);
        var changed = Field<StreamGeometry>(eq, "_strokeGeometry");
        Assert.AreNotSame(original, changed);
        Invoke(eq, "EnsureCurveCache", 640.0, 180.0);
        Assert.AreNotSame(changed, Field<StreamGeometry>(eq, "_strokeGeometry"));
    }

    [TestMethod]
    [DataRow("capture")]
    [DataRow("disabled")]
    [DataRow("unloaded")]
    public void InterruptedDrag_RestoresLatestPointWithoutWriting(string interruption)
    {
        var eq = new GraphicEqControl();
        int writes = 0;
        eq.ParameterCommitted += (_, _) => writes++;
        SetField(eq, "_dragIndex", 1);
        Field<double[]>(eq, "_freq")[1] = 4000;
        Field<double[]>(eq, "_gain")[1] = 12;
        eq.SetPoint(1, 1200, -3);
        Interrupt(eq, interruption);
        Assert.AreEqual(-1, Field<int>(eq, "_dragIndex"));
        Assert.AreEqual(1200, Field<double[]>(eq, "_freq")[1]);
        Assert.AreEqual(-3, Field<double[]>(eq, "_gain")[1]);
        Assert.AreEqual(0, writes);
    }
}
