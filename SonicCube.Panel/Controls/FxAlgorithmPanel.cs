using SonicCube.Panel.Localization;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// 通用算法效果面板(算法下拉 + 参数旋钮阵列 + 启用遮罩)。
/// 适用于 COMP(0)、INST(2)、EQ(3)、RVB(4)。切算法时下发 fxId + 各参数默认值。
/// </summary>
public sealed class FxAlgorithmPanel : UserControl
{
    private readonly int _module;
    private ComboBox _algSelect = null!;
    private FlowLayoutPanel _knobHost = null!;
    private System.Windows.Forms.Panel _mask = null!;
    private Label _title = null!;
    private readonly List<Knob> _knobs = new();
    private bool _suppressAlgEvent;

    /// <summary>请求设置 fxId(FXALG,location=module)。</summary>
    public event Action<int /*fxId*/>? AlgorithmChangeRequested;
    /// <summary>请求写效果参数(module, algId, value)。</summary>
    public event Action<int, int, float>? FxParamRequested;

    public FxAlgorithmPanel(int module)
    {
        _module = module;
        BackColor = Color.FromArgb(36, 36, 40);
        ForeColor = Color.FromArgb(220, 220, 225);
        BuildUi();
        LoadAlgorithms();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void BuildUi()
    {
        Height = 200;
        _title = new Label { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(6, 0, 0, 0) };
        _algSelect = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Height = 28, BackColor = Color.FromArgb(50, 50, 56), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _algSelect.SelectedIndexChanged += (_, _) => OnAlgorithmSelected();
        _knobHost = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(6) };
        _mask = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(160, 20, 20, 24), Visible = false };
        Controls.Add(_knobHost);
        Controls.Add(_algSelect);
        Controls.Add(_title);
        Controls.Add(_mask);
        _mask.BringToFront();
    }

    private void LoadAlgorithms()
    {
        var mod = FxCatalog.GetModule(_module);
        if (mod == null) return;
        _suppressAlgEvent = true;
        _algSelect.Items.Clear();
        foreach (var alg in mod.Algorithms)
            _algSelect.Items.Add(new AlgItem(alg));
        _suppressAlgEvent = false;
        if (_algSelect.Items.Count > 0) _algSelect.SelectedIndex = 0;
        RebuildKnobs();
    }

    private sealed record AlgItem(FxAlgorithmDef Alg)
    {
        public override string ToString() => $"{Alg.DisplayName.Trim()}  ({Alg.Title})";
    }

    private FxAlgorithmDef? CurrentAlg => (_algSelect.SelectedItem as AlgItem)?.Alg;

    private void OnAlgorithmSelected()
    {
        if (_suppressAlgEvent) return;
        RebuildKnobs();
        var alg = CurrentAlg;
        if (alg == null) return;
        // 切算法:下发 fxId + 各参数默认值(见 ui_controls_report 4.2;修正原版 Comp1 用 Comp2 默认值的 bug)。
        AlgorithmChangeRequested?.Invoke(alg.FxId);
        foreach (var p in alg.Params)
            FxParamRequested?.Invoke(_module, p.AlgId, p.Default);
    }

    private void RebuildKnobs()
    {
        _knobHost.Controls.Clear();
        foreach (var k in _knobs) k.Dispose();
        _knobs.Clear();
        var alg = CurrentAlg;
        if (alg == null) return;
        foreach (var p in alg.Params)
        {
            var knob = new Knob();
            knob.Configure(p.Min, p.Max, p.Step, p.Default, p.Name);
            int algId = p.AlgId;
            knob.ValueCommitted += v => FxParamRequested?.Invoke(_module, algId, v);
            _knobs.Add(knob);
            _knobHost.Controls.Add(knob);
        }
    }

    /// <summary>按当前 fxAlg 选中算法(不触发切换命令)。</summary>
    public void SyncAlgorithm(int fxId)
    {
        var mod = FxCatalog.GetModule(_module);
        if (mod == null) return;
        for (int i = 0; i < mod.Algorithms.Length; i++)
        {
            if (mod.Algorithms[i].FxId == fxId)
            {
                _suppressAlgEvent = true;
                _algSelect.SelectedIndex = i;
                _suppressAlgEvent = false;
                RebuildKnobs();
                break;
            }
        }
    }

    /// <summary>回显某参数值(fxParaVal)。</summary>
    public void SyncParam(int algId, float value)
    {
        var alg = CurrentAlg;
        if (alg == null) return;
        for (int i = 0; i < alg.Params.Length; i++)
            if (alg.Params[i].AlgId == algId && i < _knobs.Count)
                _knobs[i].SetValueSilently(value);
    }

    public void SetMask(bool masked) => _mask.Visible = masked;

    private void ApplyLocalization()
    {
        var mod = FxCatalog.GetModule(_module);
        _title.Text = mod?.Name ?? "";
    }
}
