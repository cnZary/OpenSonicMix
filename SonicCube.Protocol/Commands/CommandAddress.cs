namespace SonicCube.Protocol.Commands;

/// <summary>
/// Sonic Cube II 命令地址常量(见 re/PROTOCOL.md 与 re/ui_controls_report.md)。
/// 地址形式 0xLLTT00PP:PP=页(0x10 预设 / 0x11 全局 / 0x13 系统),TT=数据类型,LL=location。
/// 单项读写:完整地址 = 类型地址 + (location &lt;&lt; 24)。
/// </summary>
public static class CommandAddress
{
    // ── 整包 ────────────────────────────────────────────────
    /// <summary>请求/响应整个预设(空载荷请求)。</summary>
    public const uint Preset = 0x00000010;
    /// <summary>请求/响应全部全局参数(空载荷请求)。</summary>
    public const uint Global = 0x00000011;

    // ── 预设页(PP=0x10)─────────────────────────────────────
    /// <summary>反相开关 ×2,int32,location=通道 0/1。</summary>
    public const uint Inverse = 0x00010010;
    /// <summary>HPF 模块开关 ×2,int32,location=通道 0/1。</summary>
    public const uint HpfSwitch = 0x00020010;
    /// <summary>低切档位 ×2,int32(0=75Hz, 1=100Hz),location=通道 0/1。</summary>
    public const uint LowCut = 0x00030010;
    /// <summary>效果模块使能位图,int32,bit0..5 = COMP,GRAPHEQ,INST,EQ,RVB,CAB。
    /// 写入:location=模块号,value=0/1(按位置位/清位);确认回包载荷恒为 0,不含新位图。</summary>
    public const uint FxModuleEnable = 0x00040010;
    /// <summary>效果槽位 ×6,int32。</summary>
    public const uint FxSlot = 0x00050010;
    /// <summary>各槽算法 id(fxId) ×6,int32,location=模块号。</summary>
    public const uint FxAlgorithm = 0x00060010;
    /// <summary>效果参数:载荷 = module(4B LE) + paramIdx(4B LE) + value(float32 LE)。</summary>
    public const uint FxParameter = 0x00080010;
    /// <summary>静音 ×4,int32,location 0-3(MIC1, LINE/INST2, USB, OTG)。</summary>
    public const uint MuteSwitch = 0x00090010;
    /// <summary>独听 ×2,int32,location 0/1。</summary>
    public const uint SoloSwitch = 0x000A0010;
    /// <summary>音量推子 ×4,float32 dB(-51~+10),location 0-3。</summary>
    public const uint VolumePot = 0x000B0010;
    /// <summary>声相 ×2,float32(-50~50),location 0/1。</summary>
    public const uint Phase = 0x000C0010;
    /// <summary>混响发送量 ×2,float32(0.0~1.0),location 0/1。</summary>
    public const uint Send = 0x000D0010;
    /// <summary>内录回路 ×2,int32(0=OTG, 1=USB),location 0/1。</summary>
    public const uint Loopback = 0x000E0010;
    /// <summary>DSP 录制选择,int32(1=效果录入 dsprec, 0=原声录入 dspmon)。</summary>
    public const uint DspRecSwitch = 0x000F0010;
    /// <summary>单声道混合开关,int32 0/1。</summary>
    public const uint Mono = 0x00110010;

    // ── 全局页(PP=0x11)─────────────────────────────────────
    /// <summary>HI(model 支持,UI 无控件)。</summary>
    public const uint Hi = 0x00010011;
    /// <summary>48V 幻象电源,int32 0/1。</summary>
    public const uint Phantom48V = 0x00020011;
    /// <summary>EFX 效果总开关,int32 0/1。</summary>
    public const uint Efx = 0x00030011;
    /// <summary>混响总开关,int32 0/1(设备端按键上报)。</summary>
    public const uint ReverbOn = 0x00040011;
    /// <summary>硬件监听开关,int32 0/1。</summary>
    public const uint Monitor = 0x00050011;
    /// <summary>ON AIR(推流)开关,int32 0/1。</summary>
    public const uint OnAir = 0x00060011;
    /// <summary>主输出静音,int32 0/1。</summary>
    public const uint MasterMute = 0x00070011;
    /// <summary>自动输出(model 支持,UI 无控件)。</summary>
    public const uint AutoOutput = 0x00090011;
    /// <summary>输出电平,设备主动推送,10 × float32 dB。</summary>
    public const uint PickOut = 0x000B0011;

    // ── 系统页(PP=0x13)─────────────────────────────────────
    /// <summary>软件状态(握手第一步,请求)。</summary>
    public const uint SoftwareState = 0x00010013;
    /// <summary>固件状态(走 !%~IUSH 带校验通道,本项目不实现固件升级)。</summary>
    public const uint FirmwareState = 0x12000000;
    /// <summary>复位预设(空载荷)。</summary>
    public const uint CommandReset = 0x00020013;
    /// <summary>跳转固件升级模式(本项目不实现)。</summary>
    public const uint SysCommandJump = 0x00030013;

    /// <summary>组合带 location 的单项地址。</summary>
    public static uint WithLocation(uint baseAddress, int location)
        => baseAddress + ((uint)location << 24);

    /// <summary>从完整地址拆出 location(高 8 位)。</summary>
    public static int LocationOf(uint fullAddress) => (int)(fullAddress >> 24) & 0xFF;

    /// <summary>从完整地址拆出不带 location 的类型地址(低 24 位)。</summary>
    public static uint TypeOf(uint fullAddress) => fullAddress & 0x00FFFFFF;
}
