using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;

namespace SonicCube.Panel.Controls;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// HPF 面板(两通道共用,见 ui_controls_report 4.5)。75Hz/100Hz 互斥选择,按通道发 LOWCUT。
/// </summary>
public sealed class HpfPanel : UserControl
{
    private ToggleButton _b75_1 = null!, _b100_1 = null!, _b75_2 = null!, _b100_2 = null!;
    private Label _t1 = null!, _t2 = null!;
    private bool _suppress;

    public event Action<uint, int>? SetIntRequested;

    public HpfPanel()
    {
        BackColor = Color.FromArgb(36, 36, 40);
        ForeColor = Color.FromArgb(220, 220, 225);
        BuildUi();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void BuildUi()
    {
        Height = 120;
        _t1 = new Label { Left = 8, Top = 8, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _b75_1 = new ToggleButton { Left = 90, Top = 6, Width = 70 };
        _b100_1 = new ToggleButton { Left = 168, Top = 6, Width = 70 };
        _t2 = new Label { Left = 8, Top = 48, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _b75_2 = new ToggleButton { Left = 90, Top = 46, Width = 70 };
        _b100_2 = new ToggleButton { Left = 168, Top = 46, Width = 70 };
        Controls.AddRange(new Control[] { _t1, _b75_1, _b100_1, _t2, _b75_2, _b100_2 });

        _b75_1.CheckedChanged += v => { if (v && !_suppress) { _suppress = true; _b100_1.SetCheckedSilently(false); _suppress = false; Send(0, 0); } };
        _b100_1.CheckedChanged += v => { if (v && !_suppress) { _suppress = true; _b75_1.SetCheckedSilently(false); _suppress = false; Send(0, 1); } };
        _b75_2.CheckedChanged += v => { if (v && !_suppress) { _suppress = true; _b100_2.SetCheckedSilently(false); _suppress = false; Send(1, 0); } };
        _b100_2.CheckedChanged += v => { if (v && !_suppress) { _suppress = true; _b75_2.SetCheckedSilently(false); _suppress = false; Send(1, 1); } };
    }

    private void Send(int ch, int lowcut) =>
        SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.LowCut, ch), lowcut);

    public void ApplyState(Protocol.Models.PresetState p)
    {
        _suppress = true;
        _b75_1.SetCheckedSilently(p.LowCut[0] == 0);
        _b100_1.SetCheckedSilently(p.LowCut[0] == 1);
        _b75_2.SetCheckedSilently(p.LowCut[1] == 0);
        _b100_2.SetCheckedSilently(p.LowCut[1] == 1);
        _suppress = false;
    }

    private void ApplyLocalization()
    {
        _t1.Text = Loc.Get("ChMic1");
        _t2.Text = Loc.Get("ChLineInst2");
        _b75_1.Text = Loc.Get("Hpf75"); _b100_1.Text = Loc.Get("Hpf100");
        _b75_2.Text = Loc.Get("Hpf75"); _b100_2.Text = Loc.Get("Hpf100");
    }
}

/// <summary>
/// Global EQ(GRAPHEQ,模块 1)面板:三拖拽点 + 右侧总音量推子(UI -50~+50 ↔ 协议 0~100)。
/// </summary>
public sealed class GlobalEqModulePanel : UserControl
{
    private GlobalEqPanel _eq = null!;
    private LinearSlider _vol = null!;
    private Label _title = null!, _volLabel = null!;
    private System.Windows.Forms.Panel _mask = null!;

    /// <summary>写 EQ 参数(module=1, location, value)。</summary>
    public event Action<int, int, float>? FxParamRequested;

    public GlobalEqModulePanel()
    {
        BackColor = Color.FromArgb(36, 36, 40);
        ForeColor = Color.FromArgb(220, 220, 225);
        BuildUi();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void BuildUi()
    {
        Height = 240;
        _title = new Label { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(6, 0, 0, 0) };
        _eq = new GlobalEqPanel { Dock = DockStyle.Fill };
        _eq.ParameterCommitted += (loc, val) => FxParamRequested?.Invoke(1, loc, val);
        var right = new Panel { Dock = DockStyle.Right, Width = 80 };
        _volLabel = new Label { Dock = DockStyle.Top, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 7.5f) };
        _vol = new LinearSlider { Left = 14, Top = 20, Width = 50, Height = 180 };
        _vol.Configure(-50, 50, 0);
        _vol.ValueCommitted += v => FxParamRequested?.Invoke(1, 7, Protocol.Models.ValueMapping.GlobalEqVolToProtocol(v));
        right.Controls.Add(_vol);
        right.Controls.Add(_volLabel);
        _mask = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(160, 20, 20, 24), Visible = false };
        Controls.Add(_eq);
        Controls.Add(right);
        Controls.Add(_title);
        Controls.Add(_mask);
        _mask.BringToFront();
    }

    public void SetVolCommit(float uiValue) => FxParamRequested?.Invoke(1, 7, Protocol.Models.ValueMapping.GlobalEqVolToProtocol(uiValue));

    public void ApplyState(Protocol.Models.PresetState p)
    {
        _eq.SetPoint(0, p.GetFxPara(1, 0), p.GetFxPara(1, 1));
        _eq.SetPoint(1, p.GetFxPara(1, 2), p.GetFxPara(1, 4));
        _eq.SetQ(p.GetFxPara(1, 3));
        _eq.SetPoint(2, p.GetFxPara(1, 5), p.GetFxPara(1, 6));
        _vol.SetValueSilently((float)Protocol.Models.ValueMapping.GlobalEqVolToUi(p.GetFxPara(1, 7)));
    }

    public void SetMask(bool masked) => _mask.Visible = masked;

    private void ApplyLocalization() { _title.Text = Loc.Get("GlobalEq"); _volLabel.Text = "VOL"; }
}
