using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class ModelTests
{
    private static PresetState BuildSamplePreset()
    {
        var s = new PresetState();
        s.InverseSw[0] = 1; s.InverseSw[1] = 0;
        s.HpfSw[0] = 1; s.HpfSw[1] = 0;
        s.LowCut[0] = 0; s.LowCut[1] = 1;
        s.LoopbackSw[0] = 1; s.LoopbackSw[1] = 0;
        s.DspRecSw = 1;
        s.FxModuleEn = 0b000101; // COMP + INST
        s.FxSlot[0] = 0; s.FxSlot[1] = 1; s.FxSlot[2] = 2; s.FxSlot[3] = 3; s.FxSlot[4] = 4; s.FxSlot[5] = 5;
        for (int i = 0; i < 6; i++) s.FxAlg[i] = 0x01000000 + i;
        for (int i = 0; i < s.FxParaVal.Length; i++) s.FxParaVal[i] = i * 0.5f;
        s.MuteSw[0] = 1; s.MuteSw[1] = 0; s.MuteSw[2] = 0; s.MuteSw[3] = 1;
        s.SoloSw[0] = 0; s.SoloSw[1] = 1;
        s.VolPot[0] = -10f; s.VolPot[1] = 0f; s.VolPot[2] = 5f; s.VolPot[3] = -51f;
        s.Phase[0] = -50f; s.Phase[1] = 50f;
        s.Send[0] = 0.25f; s.Send[1] = 0.75f;
        s.MonoSw = 1;
        return s;
    }

    // 序列化样例(与 Parse 布局一致)以验证解析正确性。
    private static byte[] Serialize(PresetState s)
    {
        using var ms = new MemoryStream();
        void B(int v) => ms.WriteByte((byte)v);
        void I32(int v) { var b = BitConverter.GetBytes(v); ms.Write(b, 0, 4); }
        void F32(float v) { var b = BitConverter.GetBytes(v); ms.Write(b, 0, 4); }
        foreach (var v in s.InverseSw) B(v);
        foreach (var v in s.HpfSw) B(v);
        foreach (var v in s.LowCut) B(v);
        foreach (var v in s.LoopbackSw) B(v);
        B(s.DspRecSw);
        B(s.FxModuleEn);
        foreach (var v in s.FxSlot) B(v);
        foreach (var v in s.FxAlg) I32(v);
        foreach (var v in s.FxParaVal) F32(v);
        foreach (var v in s.MuteSw) B(v);
        foreach (var v in s.SoloSw) B(v);
        B(0); B(0); // reserve
        foreach (var v in s.VolPot) F32(v);
        foreach (var v in s.Phase) F32(v);
        foreach (var v in s.Send) F32(v);
        B(s.MonoSw); B(0); // mono 占 2 字节槽
        return ms.ToArray();
    }

    [TestMethod]
    public void Preset_Parse_RoundTrips_SampleLayout()
    {
        var src = BuildSamplePreset();
        var bytes = Serialize(src);
        var parsed = PresetState.Parse(bytes);

        CollectionAssert.AreEqual(src.InverseSw, parsed.InverseSw);
        CollectionAssert.AreEqual(src.HpfSw, parsed.HpfSw);
        CollectionAssert.AreEqual(src.LowCut, parsed.LowCut);
        CollectionAssert.AreEqual(src.LoopbackSw, parsed.LoopbackSw);
        Assert.AreEqual(src.DspRecSw, parsed.DspRecSw);
        Assert.AreEqual(src.FxModuleEn, parsed.FxModuleEn);
        CollectionAssert.AreEqual(src.FxSlot, parsed.FxSlot);
        CollectionAssert.AreEqual(src.FxAlg, parsed.FxAlg);
        CollectionAssert.AreEqual(src.FxParaVal, parsed.FxParaVal);
        CollectionAssert.AreEqual(src.MuteSw, parsed.MuteSw);
        CollectionAssert.AreEqual(src.SoloSw, parsed.SoloSw);
        CollectionAssert.AreEqual(src.VolPot, parsed.VolPot);
        CollectionAssert.AreEqual(src.Phase, parsed.Phase);
        CollectionAssert.AreEqual(src.Send, parsed.Send);
        Assert.AreEqual(src.MonoSw, parsed.MonoSw);
    }

    [TestMethod]
    public void Preset_ApplyIndividual_FxParameter_UpdatesFlatArray()
    {
        var s = new PresetState();
        var payload = Payload.FxParameter(3, 5, 42.5f);
        s.ApplyIndividual(CommandAddress.FxParameter, 0, payload);
        Assert.AreEqual(42.5f, s.GetFxPara(3, 5));
        Assert.AreEqual(42.5f, s.FxParaVal[3 * 16 + 5]);
    }

    [TestMethod]
    public void Preset_ModuleEnable_Bits()
    {
        var s = new PresetState { FxModuleEn = 0b000101 };
        Assert.IsTrue(s.IsModuleEnabled(0));  // COMP
        Assert.IsFalse(s.IsModuleEnabled(1)); // GRAPHEQ
        Assert.IsTrue(s.IsModuleEnabled(2));  // INST
    }

    [TestMethod]
    public void Global_Parse_8Bytes()
    {
        byte[] data = [1, 0, 1, 0, 1, 0, 1, 0];
        var g = GlobalState.Parse(data);
        Assert.AreEqual(1, g.HiEn);
        Assert.AreEqual(0, g.Phantom48V);
        Assert.AreEqual(1, g.Efx);
        Assert.AreEqual(0, g.ReverbOn);
        Assert.AreEqual(1, g.Monitor);
        Assert.AreEqual(0, g.OnAir);
        Assert.AreEqual(1, g.MasterMute);
        Assert.AreEqual(0, g.AutoOutput);
    }

    [TestMethod]
    public void Global_ParseMeters_10Floats()
    {
        var data = new byte[40];
        for (int i = 0; i < 10; i++)
            BitConverter.GetBytes((float)(-i * 6)).CopyTo(data, i * 4);
        var m = GlobalState.ParseMeters(data);
        Assert.AreEqual(10, m.Length);
        Assert.AreEqual(-54f, m[9]);
    }
}
