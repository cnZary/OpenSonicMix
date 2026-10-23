using System.Windows;
using System.Windows.Controls;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 输入通道(MIC1 / LINE-INST2)。
/// 通道 0: COMP/EQ/HPF + 48V + 反相 + SEND + PHASE + 音量 + MUTE + SOLO + 电平。
/// 通道 1: INST/EQ/HPF + 反相 + SEND + PHASE + 音量 + MUTE + SOLO + 电平(无 48V)。
/// 所有设备写入经事件上抛,由 MainWindow 统一经 TrayClient 发送(合并/节流在主窗口)。
/// </summary>
public partial class InputChannelView : UserControl
{
    private readonly int _ch; // 0 或 1
    private readonly bool _isMic;
    private bool _suppress;

    public event Action<uint, int>? SetIntRequested;
    public event Action<uint, float>? SetFloatRequested;
    public event Action<string>? ToggleRequested;
    public event Action<int>? ShowEffectRequested;

    public InputChannelView(int channel)
    {
        _ch = channel;
        _isMic = channel == 0;
        InitializeComponent();
        if (!_isMic)
        {
            // CH2 无 48V:HPF 横跨整行
            Btn48V.Visibility = Visibility.Collapsed;
            System.Windows.Controls.Grid.SetColumnSpan(BtnHpf, 2);
            BtnHpf.Margin = new Thickness(0);
        }
        SendKnob.Configure(0, 100, 1, 0, "SEND");
        WireEvents();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void WireEvents()
    {
        BtnComp.Click += (_, _) => { if (_suppress) return; int m = _isMic ? FxCatalog.ModuleComp : FxCatalog.ModuleInst; SendFxModuleEnable(m, BtnComp.IsChecked == true); ShowEffectRequested?.Invoke(_isMic ? 0 : 3); };
        BtnEq.Click += (_, _) => { if (_suppress) return; int m = _isMic ? FxCatalog.ModuleGraphEq : FxCatalog.ModuleEq; SendFxModuleEnable(m, BtnEq.IsChecked == true); ShowEffectRequested?.Invoke(_isMic ? 1 : 4); };
        BtnHpf.Click += (_, _) => { if (_suppress) return; SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.HpfSwitch, _ch), BtnHpf.IsChecked == true ? 1 : 0); ShowEffectRequested?.Invoke(_isMic ? 2 : 5); };
        Btn48V.Click += (_, _) => { if (_suppress) return; ToggleRequested?.Invoke("Phantom48V"); };
        BtnPhase.Click += (_, _) => { if (_suppress) return; SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Inverse, _ch), BtnPhase.IsChecked == true ? 1 : 0); };
        BtnMute.Click += (_, _) => { if (_suppress) return; SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.MuteSwitch, _ch), BtnMute.IsChecked == true ? 1 : 0); };
        BtnSolo.Click += (_, _) => { if (_suppress) return; SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.SoloSwitch, _ch), BtnSolo.IsChecked == true ? 1 : 0); };

        SendKnob.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Send, _ch), ValueMapping.SendToProtocol(v));
        Pan.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Phase, _ch), (float)v);
        Fader.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.VolumePot, _ch), (float)v);
        Fader.ValueChanging += v => FaderValue.Text = ValueMapping.FaderText(v);
    }

    private void SendFxModuleEnable(int module, bool enabled) =>
        SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.FxModuleEnable, module), enabled ? 1 : 0);

    public void ApplyState(PresetState p, GlobalState g)
    {
        _suppress = true;
        BtnComp.IsChecked = _isMic ? p.IsModuleEnabled(FxCatalog.ModuleComp) : p.IsModuleEnabled(FxCatalog.ModuleInst);
        BtnEq.IsChecked = _isMic ? p.IsModuleEnabled(FxCatalog.ModuleGraphEq) : p.IsModuleEnabled(FxCatalog.ModuleEq);
        BtnHpf.IsChecked = p.HpfSw[_ch] == 1;
        if (_isMic) Btn48V.IsChecked = g.Phantom48V == 1;
        BtnPhase.IsChecked = p.InverseSw[_ch] == 1;
        BtnMute.IsChecked = p.MuteSw[_ch] == 1;
        BtnSolo.IsChecked = p.SoloSw[_ch] == 1;
        _suppress = false;

        SendKnob.SetValueSilently(ValueMapping.SendToUi(p.Send[_ch]));
        bool mono = p.MonoSw == 1;
        Pan.IsEnabled = mono;
        Pan.SetValueSilently(p.Phase[_ch]);
        Fader.SetDbSilently(p.VolPot[_ch]);
        FaderValue.Text = ValueMapping.FaderText(Fader.Db);
    }

    public void SetMeter(float l, float r) => Meter.SetLevelDb(l, r);

    public void FailFloatWrite(uint address, float expected)
    {
        if (address == CommandAddress.WithLocation(CommandAddress.Send, _ch)
            && ValueMapping.SendToProtocol(SendKnob.Value) == expected)
            SendKnob.FailPendingWrite(SendKnob.Value);
        else if (address == CommandAddress.WithLocation(CommandAddress.Phase, _ch))
            Pan.FailPendingWrite(expected);
    }

    private void ApplyLocalization()
    {
        Title.Text = Loc.Get(_isMic ? "ChMic1" : "ChLineInst2");
        BtnComp.Content = Loc.Get(_isMic ? "Comp" : "Inst");
        BtnEq.Content = Loc.Get("Eq");
        BtnHpf.Content = Loc.Get("Hpf");
        Btn48V.Content = Loc.Get("Phantom48");
        BtnPhase.Content = "Ø";
        BtnMute.Content = Loc.Get("Mute");
        BtnSolo.Content = Loc.Get("Solo");
        SendLabel.Text = Loc.Get("Send");
    }
}
