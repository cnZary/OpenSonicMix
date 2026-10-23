using System.Windows;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// Global EQ(GRAPHEQ,模块 1)编辑区:三拖拽点图示 + 右侧总音量。
/// 总音量:UI -50~+50 ↔ 协议 0~100(GlobalEqVolToProtocol/ToUi)。
/// </summary>
public partial class GlobalEqEditorView : System.Windows.Controls.UserControl
{
    public event Action<int, int, float>? FxParamRequested;

    public GlobalEqEditorView()
    {
        InitializeComponent();
        Eq.ParameterCommitted += (loc, val) => FxParamRequested?.Invoke(FxCatalog.ModuleGraphEq, loc, val);
        Vol.ValueCommitted += v => FxParamRequested?.Invoke(FxCatalog.ModuleGraphEq, 7, ValueMapping.GlobalEqVolToProtocol(v));
        Vol.ValueChanging += v => VolValue.Text = ((int)Math.Round(v)).ToString();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    public void ApplyState(PresetState p)
    {
        Eq.SetPoint(0, p.GetFxPara(FxCatalog.ModuleGraphEq, 0), p.GetFxPara(FxCatalog.ModuleGraphEq, 1));
        Eq.SetPoint(1, p.GetFxPara(FxCatalog.ModuleGraphEq, 2), p.GetFxPara(FxCatalog.ModuleGraphEq, 4));
        Eq.SetQ(p.GetFxPara(FxCatalog.ModuleGraphEq, 3));
        Eq.SetPoint(2, p.GetFxPara(FxCatalog.ModuleGraphEq, 5), p.GetFxPara(FxCatalog.ModuleGraphEq, 6));
        double ui = ValueMapping.GlobalEqVolToUi(p.GetFxPara(FxCatalog.ModuleGraphEq, 7));
        Vol.SetValueSilently(ui);
        VolValue.Text = ((int)Math.Round(Vol.Value)).ToString();
    }

    public void SetMask(bool masked)
    {
        Mask.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
        Eq.IsEnabled = Vol.IsEnabled = !masked;
        if (masked && (Eq.IsKeyboardFocusWithin || Vol.IsKeyboardFocusWithin))
            System.Windows.Input.Keyboard.ClearFocus();
    }

    public void FailFxWrite(int module, int param, float expected)
    {
        if (module == FxCatalog.ModuleGraphEq && param == 7)
            Vol.FailPendingWrite(ValueMapping.GlobalEqVolToUi(expected));
    }

    private void ApplyLocalization()
    {
        VolLabel.Text = "VOL";
    }
}
