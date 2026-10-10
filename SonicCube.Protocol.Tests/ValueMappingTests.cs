using SonicCube.Protocol.Models;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class ValueMappingTests
{
    [TestMethod]
    public void Fader_AnchorPoints_MapExactly()
    {
        // xData/yData 锚点:db -51→pos -51, db 10→pos 10, db 0→pos -7.42857145
        Assert.AreEqual(-51, ValueMapping.FaderDbToPosition(-51), 1e-6);
        Assert.AreEqual(10, ValueMapping.FaderDbToPosition(10), 1e-6);
        Assert.AreEqual(-7.42857145, ValueMapping.FaderDbToPosition(0), 1e-6);
    }

    [TestMethod]
    public void Fader_RoundTrip_ThroughBothMaps()
    {
        foreach (double db in new[] { -51.0, -40, -30, -25, -20, -15, -10, -7, -5, -2, 0, 3, 5, 7, 10.0 })
        {
            double pos = ValueMapping.FaderDbToPosition(db);
            double back = ValueMapping.FaderPositionToDb(pos);
            Assert.AreEqual(db, back, 1e-4, $"db {db} round-trip failed");
        }
    }

    [TestMethod]
    public void Fader_SevenSegments_AreUniform()
    {
        // 七段均分:相邻 yData 间隔相等
        double y0 = ValueMapping.FaderDbToPosition(-51);
        double y1 = ValueMapping.FaderDbToPosition(-30);
        double y2 = ValueMapping.FaderDbToPosition(-20);
        Assert.AreEqual(y1 - y0, y2 - y1, 1e-6);
    }

    [TestMethod]
    public void Fader_Text_Infinity()
    {
        Assert.AreEqual("-∞", ValueMapping.FaderText(-51));
        Assert.AreEqual("-∞", ValueMapping.FaderText(-50.2));
        Assert.AreEqual("-50", ValueMapping.FaderText(-50.1));
        Assert.AreEqual("0", ValueMapping.FaderText(0));
    }

    [TestMethod]
    public void Send_Ui100_IsProtocol1()
    {
        Assert.AreEqual(1.0f, ValueMapping.SendToProtocol(100), 1e-6);
        Assert.AreEqual(0.0f, ValueMapping.SendToProtocol(0), 1e-6);
        Assert.AreEqual(50.0, ValueMapping.SendToUi(0.5f), 1e-4);
    }

    [TestMethod]
    public void EqFreq_LogScale_Anchors()
    {
        // freq 公式:10^(norm/0.92*3 + log10(2)+1)。norm=0 → 10^(1.301)=20Hz;norm=0.92 → 10^(4.301)=20000Hz
        double f0 = ValueMapping.EqPixelToFreq(0, 100);
        double fMax = ValueMapping.EqPixelToFreq(92, 100); // dx/(W*0.92)=1
        Assert.AreEqual(20.0, f0, 0.5);
        Assert.AreEqual(20000.0, fMax, 100);
    }

    [TestMethod]
    public void EqFreq_RoundTrip()
    {
        foreach (double f in new[] { 20.0, 100, 1000, 5000, 20000.0 })
        {
            double norm = ValueMapping.EqFreqToNorm(f);
            double back = ValueMapping.EqPixelToFreq(norm * 100, 100);
            Assert.AreEqual(f, back, f * 0.01, $"freq {f} round-trip failed");
        }
    }

    [TestMethod]
    public void EqGain_Anchors()
    {
        Assert.AreEqual(20.0, ValueMapping.EqPixelToGain(0, 100), 1e-6);
        Assert.AreEqual(-20.0, ValueMapping.EqPixelToGain(71, 100), 1e-6); // dy/(H*0.71)=1
    }

    [TestMethod]
    public void GlobalEqVol_Offset50()
    {
        Assert.AreEqual(50f, ValueMapping.GlobalEqVolToProtocol(0), 1e-6);
        Assert.AreEqual(0f, ValueMapping.GlobalEqVolToProtocol(-50), 1e-6);
        Assert.AreEqual(100f, ValueMapping.GlobalEqVolToProtocol(50), 1e-6);
        Assert.AreEqual(0.0, ValueMapping.GlobalEqVolToUi(50f), 1e-6);
    }

    [TestMethod]
    public void Meter_Progress()
    {
        Assert.AreEqual(1.0, ValueMapping.MeterDbToProgress(-60), 1e-9);
        Assert.AreEqual(1.0, ValueMapping.MeterDbToProgress(-100), 1e-9);
        Assert.AreEqual(0.0, ValueMapping.MeterDbToProgress(0), 1e-9);
        Assert.AreEqual(0.0, ValueMapping.MeterDbToProgress(5), 1e-9);
        Assert.AreEqual(0.5, ValueMapping.MeterDbToProgress(-30), 1e-9);
    }

    [TestMethod]
    public void FxCatalog_Counts()
    {
        Assert.AreEqual(5, FxCatalog.Modules.Count);
        int totalAlg = FxCatalog.Modules.Sum(m => m.Algorithms.Length);
        int totalParams = FxCatalog.Modules.Sum(m => m.Algorithms.Sum(a => a.Params.Length));
        Assert.AreEqual(15, totalAlg);
        Assert.AreEqual(70, totalParams);
    }

    [TestMethod]
    public void FxCatalog_FxIds_MatchReport()
    {
        Assert.AreEqual(0x00000000, FxCatalog.FindAlgorithm(0, 0)!.FxId);       // Comprosso
        Assert.AreEqual(0x0100004A, FxCatalog.FindAlgorithm(1, 0x0100004A)!.FxId); // Global EQ
        Assert.AreEqual(0x0800007A, FxCatalog.FindAlgorithm(2, 0x0800007A)!.FxId); // Acoustic Preamp 1
        Assert.AreEqual(0x0C000010, FxCatalog.FindAlgorithm(4, 0x0C000010)!.FxId); // Large Plate
    }
}
