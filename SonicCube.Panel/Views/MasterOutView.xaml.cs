using System.Windows;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 主输出区。Logo 暂时留空;主输出电平(高)+ 刻度 + L/R;
/// 效果录入/原声录入(分段,同一参数 DSPRECSW 两取值)、总静音、单声道混合(MONO)。
/// MONO 走 Tray 联动(MONO=1 + PHASE 归 0;关时恢复 -50/+50)。
/// </summary>
public partial class MasterOutView : System.Windows.Controls.UserControl
{
    private bool _suppress;

    public event Action<uint, int>? SetIntRequested;
    public event Action<string>? ToggleRequested;

    public MasterOutView()
    {
        InitializeComponent();
        BtnDspRec.Click += (_, _) => { if (!_suppress) SetDsp(1); };
        BtnDspMon.Click += (_, _) => { if (!_suppress) SetDsp(0); };
        BtnMasterMute.Click += (_, _) => { if (!_suppress) ToggleRequested?.Invoke("MasterMute"); };
        BtnMono.Click += (_, _) => { if (!_suppress) ToggleRequested?.Invoke("Mono"); };
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void SetDsp(int value)
    {
        _suppress = true;
        BtnDspRec.IsChecked = value == 1;
        BtnDspMon.IsChecked = value == 0;
        _suppress = false;
        SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.DspRecSwitch, 0), value);
    }

    public void ApplyState(PresetState p, GlobalState g)
    {
        _suppress = true;
        BtnDspRec.IsChecked = p.DspRecSw == 1;
        BtnDspMon.IsChecked = p.DspRecSw == 0;
        BtnMasterMute.IsChecked = g.MasterMute == 1;
        BtnMono.IsChecked = p.MonoSw == 1;
        _suppress = false;
    }

    public void SetMeter(float l, float r) => Meter.SetLevelDb(l, r);

    private void ApplyLocalization()
    {
        MasterTitle.Text = Loc.Get("MasterOut");
        BtnDspRec.Content = Loc.Get("DspRec");
        BtnDspMon.Content = Loc.Get("DspMon");
        BtnMasterMute.Content = Loc.Get("MasterMute");
        BtnMono.Content = Loc.Get("Mono");
    }
}
