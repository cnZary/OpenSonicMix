using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using SonicCube.Panel.Controls;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 通用算法编辑区(算法下拉 + 参数编辑器 + 使能遮罩)。EQ 模块全部参数及 WidgetType=3 使用线性推子。
/// 适用于 COMP(0)、INST(2)、EQ(3)、RVB(4)。切算法时下发 fxId + 各参数默认值。
/// 参数始终单行显示且不滚动；旋钮自适应列宽并垂直居中，EQ 推子填满参数区。
/// </summary>
public partial class AlgorithmEditorView : UserControl
{
    private readonly int _module;
    private readonly List<(FrameworkElement Element, Action<float> Sync)> _editors = new();
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
        RebuildParameters();
    }

    private void OnAlgorithmSelected()
    {
        if (_suppressAlg) return;
        RebuildParameters();
        var alg = CurrentAlg;
        if (alg == null) return;
        AlgorithmChangeRequested?.Invoke(alg.FxId);
        foreach (var p in alg.Params)
            FxParamRequested?.Invoke(_module, p.AlgId, p.Default);
    }

    private void RebuildParameters()
    {
        KnobHost.Children.Clear();
        KnobHost.ColumnDefinitions.Clear();
        KnobHost.RowDefinitions.Clear();
        _editors.Clear();
        var alg = CurrentAlg;
        if (alg == null) return;
        for (int i = 0; i < alg.Params.Length; i++)
        {
            var p = alg.Params[i];
            var editor = UsesFader(p) ? CreateFader(p) : CreateKnob(p);
            _editors.Add(editor);
            KnobHost.Children.Add(editor.Element);
        }
        ArrangeParameters();
    }

    private bool UsesFader(FxParamDef p) => _module == FxCatalog.ModuleEq || p.WidgetType == 3;

    private (FrameworkElement Element, Action<float> Sync) CreateKnob(FxParamDef p)
    {
        var knob = new KnobControl
        {
            Width = double.NaN, MaxWidth = 76,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = p.Name,
        };
        knob.Configure(p.Min, p.Max, p.Step, p.Default, p.Name);
        knob.ValueCommitted += v => FxParamRequested?.Invoke(_module, p.AlgId, (float)v);
        return (knob, v => knob.SetValueSilently(v));
    }

    private (FrameworkElement Element, Action<float> Sync) CreateFader(FxParamDef p)
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock { Text = p.Name, Style = (Style)FindResource("LabelText"), Margin = new Thickness(0, 0, 0, 4) };
        var fader = new LinearFaderControl
        {
            Minimum = p.Min, Maximum = p.Max, DefaultValue = p.Default, Value = p.Default,
            Width = 36, Height = double.NaN, MinHeight = 64, MaxHeight = 160,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(fader, p.Name);
        fader.ValueCommitted += v => FxParamRequested?.Invoke(_module, p.AlgId, (float)v);
        Grid.SetRow(fader, 1);

        var value = new TextBlock { Style = (Style)FindResource("ValueText") };
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(LinearFaderControl.Value)) { Source = fader, StringFormat = "0" });
        var readout = new Border
        {
            Style = (Style)FindResource("ReadoutPill"), Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 4, 0, 0), Child = value,
        };
        Grid.SetRow(readout, 2);
        column.Children.Add(label);
        column.Children.Add(fader);
        column.Children.Add(readout);
        return (column, v => fader.SetValueSilently(v));
    }

    private void ArrangeParameters()
    {
        if (_editors.Count == 0) return;
        KnobHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < _editors.Count; i++)
        {
            var element = _editors[i].Element;
            KnobHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            Grid.SetColumn(element, i);
            Grid.SetRow(element, 0);
            element.Margin = new Thickness(4, 0, 4, 0);
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
                if (AlgSelect.SelectedIndex != i || _editors.Count != mod.Algorithms[i].Params.Length)
                {
                    _suppressAlg = true;
                    AlgSelect.SelectedIndex = i;
                    _suppressAlg = false;
                    RebuildParameters();
                }
                break;
            }
        }
    }

    public void SyncParam(int algId, float value)
    {
        var alg = CurrentAlg;
        if (alg == null) return;
        for (int i = 0; i < alg.Params.Length && i < _editors.Count; i++)
            if (alg.Params[i].AlgId == algId)
                _editors[i].Sync(value);
    }

    public void SetMask(bool masked) => Mask.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
}
