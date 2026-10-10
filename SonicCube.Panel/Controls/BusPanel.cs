using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;

namespace SonicCube.Panel.Controls;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// 总线与状态栏面板(见 ui_controls_report 6/7)。
/// DSP REC/MON(同一参数两取值)、总静音、MONO(联动 PHASE)、EFX/MONITOR/ONAIR、主输出电平。
/// </summary>
public sealed class BusPanel : UserControl
{
    private ToggleButton _btnDspRec = null!, _btnDspMon = null!, _btnMono = null!, _btnMasterMute = null!;
    private ToggleButton _btnEfx = null!, _btnMonitor = null!, _btnOnAir = null!;
    private StereoMeter _masterMeter = null!;
    private Label _masterLabel = null!;
    private bool _suppressDsp;

    public event Action<uint, int>? SetIntRequested;
    public event Action<string>? ToggleRequested; // MONO / 总开关用 toggle 语义

    public BusPanel()
    {
        BackColor = Color.FromArgb(36, 36, 40);
        ForeColor = Color.FromArgb(220, 220, 225);
        BuildUi();
        WireEvents();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void BuildUi()
    {
        Height = 90;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(6, 8, 0, 0), AutoSize = true };
        _btnDspRec = new ToggleButton { Width = 78 };
        _btnDspMon = new ToggleButton { Width = 78 };
        _btnMono = new ToggleButton { Width = 62 };
        _btnMasterMute = new ToggleButton { Width = 78 };
        _btnEfx = new ToggleButton { Width = 56 };
        _btnMonitor = new ToggleButton { Width = 76 };
        _btnOnAir = new ToggleButton { Width = 70 };
        flow.Controls.AddRange(new Control[] { _btnDspRec, _btnDspMon, _btnMono, _btnMasterMute, _btnEfx, _btnMonitor, _btnOnAir });

        var right = new Panel { Dock = DockStyle.Right, Width = 120 };
        _masterLabel = new Label { Dock = DockStyle.Top, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 8) };
        _masterMeter = new StereoMeter { Left = 40, Top = 20, Width = 40, Height = 56 };
        right.Controls.Add(_masterMeter);
        right.Controls.Add(_masterLabel);

        Controls.Add(flow);
        Controls.Add(right);
    }

    private void WireEvents()
    {
        // DSP REC = 1,DSP MON = 0(同一参数 DSPRECSW,互斥)。
        _btnDspRec.CheckedChanged += v => { if (v && !_suppressDsp) { SetDsp(1); } };
        _btnDspMon.CheckedChanged += v => { if (v && !_suppressDsp) { SetDsp(0); } };
        _btnMono.CheckedChanged += _ => ToggleRequested?.Invoke("Mono");
        _btnMasterMute.CheckedChanged += _ => ToggleRequested?.Invoke("MasterMute");
        _btnEfx.CheckedChanged += _ => ToggleRequested?.Invoke("Efx");
        _btnMonitor.CheckedChanged += _ => ToggleRequested?.Invoke("Monitor");
        _btnOnAir.CheckedChanged += _ => ToggleRequested?.Invoke("OnAir");
    }

    private void SetDsp(int value)
    {
        _suppressDsp = true;
        _btnDspRec.SetCheckedSilently(value == 1);
        _btnDspMon.SetCheckedSilently(value == 0);
        _suppressDsp = false;
        SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.DspRecSwitch, 0), value);
    }

    public void ApplyState(Protocol.Models.PresetState p, Protocol.Models.GlobalState g, float[]? meters)
    {
        _suppressDsp = true;
        _btnDspRec.SetCheckedSilently(p.DspRecSw == 1);
        _btnDspMon.SetCheckedSilently(p.DspRecSw == 0);
        _suppressDsp = false;
        _btnMono.SetCheckedSilently(p.MonoSw == 1);
        _btnMasterMute.SetCheckedSilently(g.MasterMute == 1);
        _btnEfx.SetCheckedSilently(g.Efx == 1);
        _btnMonitor.SetCheckedSilently(g.Monitor == 1);
        _btnOnAir.SetCheckedSilently(g.OnAir == 1);
        if (meters != null) _masterMeter.SetLevel(meters[8], meters[9]);
    }

    public void SetMasterMeter(float l, float r) => _masterMeter.SetLevel(l, r);

    private void ApplyLocalization()
    {
        _btnDspRec.Text = Loc.Get("DspRec");
        _btnDspMon.Text = Loc.Get("DspMon");
        _btnMono.Text = Loc.Get("Mono");
        _btnMasterMute.Text = Loc.Get("MasterMute");
        _btnEfx.Text = Loc.Get("Efx");
        _btnMonitor.Text = Loc.Get("Monitor");
        _btnOnAir.Text = Loc.Get("OnAir");
        _masterLabel.Text = Loc.Get("MasterOut");
    }
}
