using System.Windows;
using System.Windows.Controls;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 混响区(模块 4 = RVB)。标题 + 算法下拉 + 参数旋钮(Mix、Decay)。
/// 截图未显示但已确认的 Mix 参数保留。UI 无混响开关(设备端按键上报),rvbOn=false 时遮罩。
/// </summary>
public partial class ReverbView : UserControl
{
    private readonly List<Controls.KnobControl> _knobs = new();
    private bool _suppressAlg;

    public event Action<int /*fxId*/>? AlgorithmChangeRequested;
    public event Action<int, int, float>? FxParamRequested;

    private sealed record AlgItem(FxAlgorithmDef Alg)
    {
        public override string ToString() => $"{Alg.DisplayName.Trim()}  ({Alg.Title})";
    }

    public ReverbView()
    {
        InitializeComponent();
        AlgSelect.SelectionChanged += (_, _) => OnAlgorithmSelected();
        LoadAlgorithms();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private FxAlgorithmDef? CurrentAlg => (AlgSelect.SelectedItem as AlgItem)?.Alg;

    private void LoadAlgorithms()
    {
        var mod = FxCatalog.GetModule(FxCatalog.ModuleRvb);
        if (mod == null) return;
        _suppressAlg = true;
        AlgSelect.Items.Clear();
        foreach (var alg in mod.Algorithms) AlgSelect.Items.Add(new AlgItem(alg));
        _suppressAlg = false;
        if (AlgSelect.Items.Count > 0) AlgSelect.SelectedIndex = 0;
        RebuildKnobs();
    }

    private void OnAlgorithmSelected()
    {
        if (_suppressAlg) return;
        RebuildKnobs();
        var alg = CurrentAlg;
        if (alg == null) return;
        AlgorithmChangeRequested?.Invoke(alg.FxId);
        foreach (var p in alg.Params)
            FxParamRequested?.Invoke(FxCatalog.ModuleRvb, p.AlgId, p.Default);
    }

    private void RebuildKnobs()
    {
        KnobHost.Children.Clear();
        KnobHost.ColumnDefinitions.Clear();
        _knobs.Clear();
        var alg = CurrentAlg;
        if (alg == null) return;
        for (int i = 0; i < alg.Params.Length; i++)
            KnobHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < alg.Params.Length; i++)
        {
            var p = alg.Params[i];
            var knob = new Controls.KnobControl { Width = 64, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
            knob.Configure(p.Min, p.Max, p.Step, p.Default, p.Name);
            int algId = p.AlgId;
            knob.ValueCommitted += v => FxParamRequested?.Invoke(FxCatalog.ModuleRvb, algId, (float)v);
            Grid.SetColumn(knob, i);
            _knobs.Add(knob);
            KnobHost.Children.Add(knob);
        }
    }

    public void SyncAlgorithm(int fxId)
    {
        var mod = FxCatalog.GetModule(FxCatalog.ModuleRvb);
        if (mod == null) return;
        for (int i = 0; i < mod.Algorithms.Length; i++)
        {
            if (mod.Algorithms[i].FxId == fxId)
            {
                if (AlgSelect.SelectedIndex != i || _knobs.Count != mod.Algorithms[i].Params.Length)
                {
                    _suppressAlg = true;
                    AlgSelect.SelectedIndex = i;
                    _suppressAlg = false;
                    RebuildKnobs();
                }
                break;
            }
        }
    }

    public void ApplyState(PresetState p)
    {
        SyncAlgorithm(p.FxAlg[FxCatalog.ModuleRvb]);
        var alg = CurrentAlg;
        if (alg != null)
            for (int i = 0; i < alg.Params.Length && i < _knobs.Count; i++)
                _knobs[i].SetValueSilently(p.GetFxPara(FxCatalog.ModuleRvb, alg.Params[i].AlgId));
    }

    public void SetMask(bool masked) => Mask.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyLocalization()
    {
        Title.Text = Loc.Get("Reverb");
    }
}
