using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;

namespace SonicCube.Panel.Controls;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// 输入通道面板(MIC1 / LINE-INST2)。
/// 通道 0: COMP/EQ/HPF 模块使能 + 48V + 反相 + SEND + PHASE + 音量 + MUTE + SOLO + 电平。
/// 通道 1: INST/EQ/HPF + 反相 + SEND + PHASE + 音量 + MUTE + SOLO + 电平(无 48V)。
/// </summary>
public sealed class InputChannelPanel : UserControl
{
    private readonly int _ch; // 0 或 1
    private readonly bool _isMic;

    private ToggleButton _btnComp = null!, _btnEq = null!, _btnHpf = null!, _btn48V = null!, _btnPhase = null!;
    private ToggleButton _btnMute = null!, _btnSolo = null!;
    private Knob _sendKnob = null!;
    private PanSlider _pan = null!;
    private VolumeFader _fader = null!;
    private StereoMeter _meter = null!;
    private Label _title = null!, _sendLabel = null!, _panLabel = null!;

    /// <summary>请求写协议:int32。</summary>
    public event Action<uint, int>? SetIntRequested;
    /// <summary>请求写协议:float32(完整地址)。</summary>
    public event Action<uint, float>? SetFloatRequested;
    /// <summary>请求切换 48V(toggle 语义)。</summary>
    public event Action? Toggle48VRequested;
    /// <summary>请求切到指定效果页(MIC1: COMP=0/EQ=1/HPF=2;CH2: INST=3/EQ=4/HPF=5)。</summary>
    public event Action<int>? ShowEffectRequested;

    public InputChannelPanel(int channel)
    {
        _ch = channel;
        _isMic = channel == 0;
        BackColor = Color.FromArgb(36, 36, 40);
        ForeColor = Color.FromArgb(220, 220, 225);
        BuildUi();
        WireEvents();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void BuildUi()
    {
        Width = 200; Height = 560;
        _title = new Label { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10, FontStyle.Bold) };

        var moduleRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6, 3, 0, 0), WrapContents = false };
        _btnComp = new ToggleButton { Width = 58 };
        _btnEq = new ToggleButton { Width = 46 };
        _btnHpf = new ToggleButton { Width = 46 };
        moduleRow.Controls.Add(_btnComp);
        moduleRow.Controls.Add(_btnEq);
        moduleRow.Controls.Add(_btnHpf);

        var swRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6, 3, 0, 0), WrapContents = false };
        _btn48V = new ToggleButton { Width = 46 };
        _btnPhase = new ToggleButton { Width = 58 };
        if (_isMic) swRow.Controls.Add(_btn48V);
        swRow.Controls.Add(_btnPhase);

        var sendCol = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 96, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 2, 0, 0) };
        _sendLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 8) };
        _sendKnob = new Knob { Width = 80, Height = 76 };
        sendCol.Controls.Add(_sendLabel);
        sendCol.Controls.Add(_sendKnob);

        var panCol = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 2, 0, 0) };
        _panLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 8) };
        _pan = new PanSlider { Width = 150 };
        panCol.Controls.Add(_panLabel);
        panCol.Controls.Add(_pan);

        // 下部:推子 + MUTE/SOLO + 电平
        var bottom = new Panel { Dock = DockStyle.Fill };
        var msRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6, 3, 0, 0), WrapContents = false };
        _btnMute = new ToggleButton { Width = 58 };
        _btnSolo = new ToggleButton { Width = 58 };
        msRow.Controls.Add(_btnMute);
        msRow.Controls.Add(_btnSolo);
        _fader = new VolumeFader { Left = 30, Top = 4, Width = 56, Height = 220 };
        _meter = new StereoMeter { Left = 130, Top = 4, Width = 36, Height = 220 };
        bottom.Controls.Add(_fader);
        bottom.Controls.Add(_meter);
        bottom.Controls.Add(msRow);

        Controls.Add(bottom);
        Controls.Add(panCol);
        Controls.Add(sendCol);
        Controls.Add(swRow);
        Controls.Add(moduleRow);
        Controls.Add(_title);
    }

    private void WireEvents()
    {
        // 模块使能(FXMODULEEN):location = 模块号。MIC1: COMP=0,GRAPHEQ=1;CH2: INST=2,EQ=3。HPF 单独地址。
        _btnComp.CheckedChanged += v =>
        {
            int module = _isMic ? 0 : 2;
            SendFxModuleEnable(module, v);
            ShowEffectRequested?.Invoke(_isMic ? 0 : 3);
        };
        _btnEq.CheckedChanged += v =>
        {
            int module = _isMic ? 1 : 3;
            SendFxModuleEnable(module, v);
            ShowEffectRequested?.Invoke(_isMic ? 1 : 4);
        };
        _btnHpf.CheckedChanged += v =>
        {
            SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.HpfSwitch, _ch), v ? 1 : 0);
            ShowEffectRequested?.Invoke(_isMic ? 2 : 5);
        };
        _btn48V.CheckedChanged += _ => Toggle48VRequested?.Invoke();
        _btnPhase.CheckedChanged += v =>
            SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Inverse, _ch), v ? 1 : 0);
        _btnMute.CheckedChanged += v =>
            SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.MuteSwitch, _ch), v ? 1 : 0);
        _btnSolo.CheckedChanged += v =>
            SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.SoloSwitch, _ch), v ? 1 : 0);

        _sendKnob.Configure(0, 100, 1, 0, "SEND");
        _sendKnob.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Send, _ch), Protocol.Models.ValueMapping.SendToProtocol(v));

        _pan.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Phase, _ch), v);

        _fader.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.VolumePot, _ch), v);
    }

    private void SendFxModuleEnable(int module, bool enabled)
    {
        // FXMODULEEN 是位图:单项写入时 location=模块号,value=0/1(见 ui_controls_report)。
        SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.FxModuleEnable, module), enabled ? 1 : 0);
    }

    // ── 状态回显(由主窗体从快照/更新喂入)────────────────────

    public void ApplyState(Protocol.Models.PresetState p, Protocol.Models.GlobalState g, float[]? meters)
    {
        bool comp = _isMic ? p.IsModuleEnabled(0) : p.IsModuleEnabled(2);
        bool eq = _isMic ? p.IsModuleEnabled(1) : p.IsModuleEnabled(3);
        _btnComp.SetCheckedSilently(comp);
        _btnEq.SetCheckedSilently(eq);
        _btnHpf.SetCheckedSilently(p.HpfSw[_ch] == 1);
        if (_isMic) _btn48V.SetCheckedSilently(g.Phantom48V == 1);
        _btnPhase.SetCheckedSilently(p.InverseSw[_ch] == 1);
        _btnMute.SetCheckedSilently(p.MuteSw[_ch] == 1);
        _btnSolo.SetCheckedSilently(p.SoloSw[_ch] == 1);
        _sendKnob.SetValueSilently((float)Protocol.Models.ValueMapping.SendToUi(p.Send[_ch]));
        bool mono = p.MonoSw == 1;
        _pan.Enabled = mono;
        _pan.SetValueSilently(p.Phase[_ch]);
        _fader.SetDbSilently(p.VolPot[_ch]);
        if (meters != null)
        {
            int mi = _ch * 2;
            _meter.SetLevel(meters[mi], meters[mi + 1]);
        }
    }

    public void SetMeter(float l, float r) => _meter.SetLevel(l, r);

    private void ApplyLocalization()
    {
        _title.Text = Loc.Get(_isMic ? "ChMic1" : "ChLineInst2");
        _btnComp.Text = Loc.Get(_isMic ? "Comp" : "Inst");
        _btnEq.Text = Loc.Get("Eq");
        _btnHpf.Text = Loc.Get("Hpf");
        _btn48V.Text = Loc.Get("Phantom48");
        _btnPhase.Text = "Ø";
        _btnPhase.ActiveText = Loc.Get("PhaseInvert");
        _btnMute.Text = Loc.Get("Mute");
        _btnSolo.Text = Loc.Get("Solo");
        _sendLabel.Text = Loc.Get("Send");
        _panLabel.Text = Loc.Get("Pan");
    }
}
