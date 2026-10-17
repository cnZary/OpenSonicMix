using System.Windows;
using System.Windows.Controls;
using SonicCube.Panel.Controls;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 通用算法编辑区(算法下拉 + 参数旋钮阵列 + 使能遮罩)。
/// 适用于 COMP(0)、INST(2)、EQ(3)、RVB(4)。切算法时下发 fxId + 各参数默认值。
/// 参数较多时编辑区内局部滚动,不改变主布局。
/// </summary>
public partial class AlgorithmEditorView : UserControl
{
    private readonly int _module;
    private readonly List<KnobControl> _knobs = new();
    private bool _suppressAlg;

    public event Action<int /*fxId*/>? AlgorithmChangeRequested;
    public event Action<int, int, float>? FxParamRequested;

    private sealed record AlgItem(FxAlgorithmDef Alg)
    {
        public override string ToString() => $"{Alg.DisplayName.Trim()}  ({Alg.Title})";
    }

    public AlgorithmEditorView(int module)
    {
        _module = module;
        InitializeComponent();
        AlgSelect.SelectionChanged += (_, _) => OnAlgorithmSelected();
        // 整框点击展开下拉(自定义模板为非交互,这里补点击);点到弹出项时不拦截。
        AlgSelect.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (AlgSelect.IsDropDownOpen) return; // 已展开,让选项正常接收点击
            AlgSelect.IsDropDownOpen = true;
            e.Handled = true;
        };
        LoadAlgorithms();
    }

    private FxAlgorithmDef? CurrentAlg => (AlgSelect.SelectedItem as AlgItem)?.Alg;

    private void LoadAlgorithms()
    {
        var mod = FxCatalog.GetModule(_module);
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
            FxParamRequested?.Invoke(_module, p.AlgId, p.Default);
    }

    private void RebuildKnobs()
    {
        KnobHost.Children.Clear();
        KnobHost.ColumnDefinitions.Clear();
        _knobs.Clear();
        var alg = CurrentAlg;
        if (alg == null) return;
        // 均分列:每个旋钮占一等宽列并居中,数量少时间隔自然拉大
        for (int i = 0; i < alg.Params.Length; i++)
            KnobHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < alg.Params.Length; i++)
        {
            var p = alg.Params[i];
            var knob = new KnobControl { HorizontalAlignment = HorizontalAlignment.Center };
            knob.Configure(p.Min, p.Max, p.Step, p.Default, p.Name);
            int algId = p.AlgId;
            knob.ValueCommitted += v => FxParamRequested?.Invoke(_module, algId, (float)v);
            Grid.SetColumn(knob, i);
            _knobs.Add(knob);
            KnobHost.Children.Add(knob);
        }
    }

    public void SyncAlgorithm(int fxId)
    {
        var mod = FxCatalog.GetModule(_module);
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

    public void SyncParam(int algId, float value)
    {
        var alg = CurrentAlg;
        if (alg == null) return;
        for (int i = 0; i < alg.Params.Length && i < _knobs.Count; i++)
            if (alg.Params[i].AlgId == algId)
                _knobs[i].SetValueSilently(value);
    }

    public void SetMask(bool masked) => Mask.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
}
