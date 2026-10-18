using System.Windows;
using System.Windows.Controls;
using SonicCube.Panel.Controls;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 输入效果编辑区。顶部同一行显示 MIC1 / LINE/INST 2 页签与重置操作;
/// 左侧窄模块选择列;右侧当前模块编辑区(算法下拉 + 参数旋钮)。
/// 页签切换只改编辑对象,不发协议。完全重置为整台设备复位(0x00020013),需确认;
/// 重置当前模块 = 关闭该模块开关并把当前算法参数写回默认值,需确认。
/// 非首屏模块编辑区按需创建(延迟实例化)。
/// </summary>
public partial class EffectSectionView : UserControl
{
    // UI 页:0=COMP,1=MIC1 EQ,2=HPF;3=INST,4=CH2 EQ,5=HPF(ch2)
    private int _page;          // 0=MIC1, 1=CH2
    private int _moduleUi;      // 当前模块 UI 索引
    private bool _suppressTab;

    private AlgorithmEditorView? _comp, _inst, _eq2;
    private GlobalEqEditorView? _graphEq;
    private UIElement? _hpf;

    public event Action<int /*fxId*/>? CompAlg, InstAlg, EqAlg;
    public event Action<int, int, float>? FxParamRequested;
    public event Action<uint, int>? SetIntRequested;
    public event Action<int /*uiIndex*/>? ResetRequested;
    public event Action? FullResetRequested;

    public EffectSectionView()
    {
        InitializeComponent();
        BuildTabs();
        BuildModuleList();
        ResetBtn.Click += (_, _) => ResetRequested?.Invoke(_moduleUi);
        FullResetBtn.Click += (_, _) => FullResetRequested?.Invoke();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
        SelectPage(0);
        SelectModule(0);
    }

    private void BuildTabs()
    {
        AddTab("ChMic1", 0);
        AddTab("ChLineInst2", 1);
    }

    private void AddTab(string key, int page)
    {
        var b = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = Loc.Get(key),
            Style = (Style)FindResource("ModuleTab"),
            Tag = page,
        };
        b.Click += (_, _) => { if (!_suppressTab) SelectPage(page); };
        TabHost.Children.Add(b);
    }

    private void BuildModuleList()
    {
        ModuleList.Children.Clear();
        if (_page == 0)
        {
            AddModuleButton("Comp", 0);
            AddModuleButton("Eq", 1);
            AddModuleButton("Hpf", 2);
        }
        else
        {
            AddModuleButton("Inst", 3);
            AddModuleButton("Eq", 4);
            AddModuleButton("Hpf", 5);
        }
    }

    private void AddModuleButton(string key, int uiIndex)
    {
        var b = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = Loc.Get(key),
            Style = (Style)FindResource("NavModuleToggle"),
            Width = 72,
            Height = 32,
            FontSize = 12,
            Margin = new Thickness(0, ModuleList.Children.Count == 0 ? 0 : 8, 0, 0),
            Tag = uiIndex,
        };
        b.Click += (_, _) => { if (!_suppressTab) SelectModule(uiIndex); };
        ModuleList.Children.Add(b);
    }

    public void SelectPage(int page)
    {
        if (_page == page && TabHost.Children.Count > 0) { RefreshTabStates(); return; }
        _page = page;
        BuildModuleList();
        RefreshTabStates();
        // 页签切换后选该页第一个模块
        SelectModule(page == 0 ? 0 : 3);
    }

    public void SelectModule(int uiIndex)
    {
        if (_moduleUi == uiIndex && EditorHost.Content != null) { RefreshModuleStates(); return; }
        _moduleUi = uiIndex;
        RefreshModuleStates();
        EditorHost.Content = uiIndex switch
        {
            0 => (_comp ??= CreateAlg(FxCatalog.ModuleComp, a => CompAlg?.Invoke(a))),
            1 => (_graphEq ??= CreateGraphEq()),
            2 or 5 => EnsureHpf(),
            3 => (_inst ??= CreateAlg(FxCatalog.ModuleInst, a => InstAlg?.Invoke(a))),
            4 => (_eq2 ??= CreateAlg(FxCatalog.ModuleEq, a => EqAlg?.Invoke(a))),
            _ => null,
        };
        SyncCurrentEditor();
    }

    /// <summary>同步当前可见编辑器的算法与参数(懒加载页首次显示时)。</summary>
    private void SyncCurrentEditor()
    {
        var p = _lastState;
        if (p == null) return;
        if (_moduleUi is 2 or 5)
        {
            SyncHpf(p);
            return;
        }
        int module = _moduleUi switch { 0 => 0, 1 => 1, 3 => 2, 4 => 3, _ => -1 };
        if (module < 0) return;
        if (EditorHost.Content is AlgorithmEditorView av)
        {
            av.SyncAlgorithm(p.FxAlg[module]);
            SyncFxParams(av, module, p);
            av.SetMask(!p.IsModuleEnabled(module));
        }
        else if (EditorHost.Content is GlobalEqEditorView gv)
        {
            gv.ApplyState(p);
            gv.SetMask(!p.IsModuleEnabled(module));
        }
    }

    private AlgorithmEditorView CreateAlg(int module, Action<int> onAlg)
    {
        var v = new AlgorithmEditorView(module);
        v.AlgorithmChangeRequested += fxId => onAlg(fxId);
        v.FxParamRequested += (m, id, val) => FxParamRequested?.Invoke(m, id, val);
        return v;
    }

    private GlobalEqEditorView CreateGraphEq()
    {
        var v = new GlobalEqEditorView();
        v.FxParamRequested += (m, id, val) => FxParamRequested?.Invoke(m, id, val);
        return v;
    }

    private UIElement EnsureHpf() => _hpf ??= CreateHpf();

    private UIElement CreateHpf()
    {
        // HPF 选择:每通道 75Hz / 100Hz 互斥(LOWCUT),未开启 HpfSw 时盖 BYPASS 遮罩。
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        _hpfBtns = new System.Windows.Controls.Primitives.ToggleButton[2, 2];
        _hpfLabels = new TextBlock[2];
        _hpfMasks = new Border[2];
        for (int ch = 0; ch < 2; ch++)
        {
            var cardGrid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            var card = new Border
            {
                Style = (Style)FindResource("ChannelHeaderBadge"),
                Padding = new Thickness(12, 8, 12, 8),
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Style = (Style)FindResource("LabelText"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Text = Loc.Get(ch == 0 ? "ChMic1" : "ChLineInst2"),
            };
            _hpfLabels[ch] = label;
            row.Children.Add(label);
            var btnHost = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(btnHost, 1);
            for (int opt = 0; opt < 2; opt++)
            {
                var b = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = opt == 0 ? "75 Hz" : "100 Hz",
                    Style = (Style)FindResource("ChannelToggle"),
                    Width = 72,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                int cch = ch, copt = opt;
                b.Click += (_, _) => OnHpfSelected(cch, copt);
                _hpfBtns[ch, opt] = b;
                btnHost.Children.Add(b);
            }
            row.Children.Add(btnHost);
            card.Child = row;
            cardGrid.Children.Add(card);

            var mask = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD9, 0x20, 0x20, 0x20)),
                CornerRadius = new CornerRadius(4),
                Visibility = Visibility.Collapsed,
                Child = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("BrushControl"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BrushDivider"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(12, 4, 12, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "BYPASS",
                        FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                    },
                },
            };
            _hpfMasks[ch] = mask;
            cardGrid.Children.Add(mask);
            panel.Children.Add(cardGrid);
        }
        return panel;
    }

    private System.Windows.Controls.Primitives.ToggleButton[,]? _hpfBtns;
    private TextBlock[]? _hpfLabels;
    private Border[]? _hpfMasks;
    private bool _suppressHpf;

    private void OnHpfSelected(int ch, int opt)
    {
        if (_suppressHpf) return;
        _suppressHpf = true;
        if (_hpfBtns != null)
        {
            _hpfBtns[ch, opt].IsChecked = true;
            _hpfBtns[ch, 1 - opt].IsChecked = false;
        }
        _suppressHpf = false;
        SetIntRequested?.Invoke(Protocol.Commands.CommandAddress.WithLocation(Protocol.Commands.CommandAddress.LowCut, ch), opt);
    }

    /// <summary>外部请求切到指定模块(用于输入通道 COMP/EQ/HPF 按钮联动,不发协议)。</summary>
    public void ShowEffect(int uiIndex)
    {
        int page = uiIndex <= 2 ? 0 : 1;
        if (page != _page)
        {
            _page = page;
            BuildModuleList();
            RefreshTabStates();
        }
        SelectModule(uiIndex);
    }

    private PresetState? _lastState;

    public void ApplyState(PresetState p)
    {
        _lastState = p;
        _comp?.SetMask(!p.IsModuleEnabled(FxCatalog.ModuleComp));
        _graphEq?.SetMask(!p.IsModuleEnabled(FxCatalog.ModuleGraphEq));
        _inst?.SetMask(!p.IsModuleEnabled(FxCatalog.ModuleInst));
        _eq2?.SetMask(!p.IsModuleEnabled(FxCatalog.ModuleEq));

        _comp?.SyncAlgorithm(p.FxAlg[FxCatalog.ModuleComp]);
        _inst?.SyncAlgorithm(p.FxAlg[FxCatalog.ModuleInst]);
        _eq2?.SyncAlgorithm(p.FxAlg[FxCatalog.ModuleEq]);
        SyncFxParams(_comp, FxCatalog.ModuleComp, p);
        SyncFxParams(_inst, FxCatalog.ModuleInst, p);
        SyncFxParams(_eq2, FxCatalog.ModuleEq, p);
        _graphEq?.ApplyState(p);

        SyncHpf(p);
    }

    private void SyncHpf(PresetState p)
    {
        if (_hpfBtns == null) return;
        _suppressHpf = true;
        for (int ch = 0; ch < 2; ch++)
        {
            _hpfBtns[ch, 0].IsChecked = p.LowCut[ch] == 0;
            _hpfBtns[ch, 1].IsChecked = p.LowCut[ch] == 1;
            if (_hpfMasks != null)
                _hpfMasks[ch].Visibility = p.HpfSw[ch] == 1 ? Visibility.Collapsed : Visibility.Visible;
        }
        _suppressHpf = false;
    }

    private static void SyncFxParams(AlgorithmEditorView? panel, int module, PresetState p)
    {
        if (panel == null) return;
        var mod = FxCatalog.GetModule(module);
        if (mod == null) return;
        foreach (var alg in mod.Algorithms)
            foreach (var param in alg.Params)
                panel.SyncParam(param.AlgId, p.GetFxPara(module, param.AlgId));
    }

    private void RefreshTabStates()
    {
        _suppressTab = true;
        foreach (var c in TabHost.Children)
            if (c is System.Windows.Controls.Primitives.ToggleButton b)
                b.IsChecked = (int)b.Tag == _page;
        _suppressTab = false;
    }

    private void RefreshModuleStates()
    {
        _suppressTab = true;
        foreach (var c in ModuleList.Children)
            if (c is System.Windows.Controls.Primitives.ToggleButton b)
                b.IsChecked = (int)b.Tag == _moduleUi;
        _suppressTab = false;
    }

    private void ApplyLocalization()
    {
        ResetBtn.Content = Loc.Get("Reset");
        FullResetBtn.Content = Loc.Get("ResetAll");
        if (TabHost.Children.Count >= 2)
        {
            ((System.Windows.Controls.Primitives.ToggleButton)TabHost.Children[0]).Content = Loc.Get("ChMic1");
            ((System.Windows.Controls.Primitives.ToggleButton)TabHost.Children[1]).Content = Loc.Get("ChLineInst2");
        }
        if (_hpfLabels is { Length: >= 2 })
        {
            _hpfLabels[0].Text = Loc.Get("ChMic1");
            _hpfLabels[1].Text = Loc.Get("ChLineInst2");
        }
        BuildModuleList();
        RefreshModuleStates();
    }
}
