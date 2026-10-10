using SonicCube.Panel.Localization;

namespace SonicCube.Panel.Controls;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// 插入效果区容器(见 ui_controls_report 4.1)。
/// MIC1 页: COMP(0)/Global EQ(1)/HPF(2);CH2 页: INST(3)/EQ(4)/HPF(5)。
/// 点击模块标签只切页,不发协议。RVB 单独成页。
/// </summary>
public sealed class FxSectionPanel : UserControl
{
    public FxAlgorithmPanel CompPanel { get; }
    public GlobalEqModulePanel GraphEqPanel { get; }
    public FxAlgorithmPanel InstPanel { get; }
    public FxAlgorithmPanel EqPanel { get; }
    public FxAlgorithmPanel RvbPanel { get; }
    public HpfPanel HpfPanelControl { get; }

    private TabControl _tabs = null!;
    private TabPage _tComp = null!, _tGraphEq = null!, _tInst = null!, _tEq = null!, _tRvb = null!, _tHpf = null!;

    public FxSectionPanel()
    {
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.FromArgb(220, 220, 225);

        CompPanel = new FxAlgorithmPanel(0) { Dock = DockStyle.Fill };
        GraphEqPanel = new GlobalEqModulePanel { Dock = DockStyle.Fill };
        InstPanel = new FxAlgorithmPanel(2) { Dock = DockStyle.Fill };
        EqPanel = new FxAlgorithmPanel(3) { Dock = DockStyle.Fill };
        RvbPanel = new FxAlgorithmPanel(4) { Dock = DockStyle.Fill };
        HpfPanelControl = new HpfPanel { Dock = DockStyle.Fill };

        _tabs = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons };
        _tComp = new TabPage();
        _tGraphEq = new TabPage();
        _tInst = new TabPage();
        _tEq = new TabPage();
        _tRvb = new TabPage();
        _tHpf = new TabPage();
        _tComp.Controls.Add(CompPanel);
        _tGraphEq.Controls.Add(GraphEqPanel);
        _tInst.Controls.Add(InstPanel);
        _tEq.Controls.Add(EqPanel);
        _tRvb.Controls.Add(RvbPanel);
        _tHpf.Controls.Add(HpfPanelControl);
        _tabs.TabPages.AddRange(new[] { _tComp, _tGraphEq, _tInst, _tEq, _tRvb, _tHpf });
        Controls.Add(_tabs);
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    /// <summary>按 UI 索引切页(0=COMP,1=MIC1 EQ,2=HPF,3=INST,4=CH2 EQ,5=HPF)。</summary>
    public void ShowEffect(int uiIndex)
    {
        _tabs.SelectedIndex = uiIndex switch
        {
            0 => 0, // COMP
            1 => 1, // MIC1 Global EQ
            2 => 5, // HPF
            3 => 2, // INST
            4 => 3, // CH2 EQ
            5 => 5, // HPF
            _ => 0,
        };
    }

    public void ApplyState(Protocol.Models.PresetState p)
    {
        // 模块使能遮罩
        CompPanel.SetMask(!p.IsModuleEnabled(0));
        GraphEqPanel.SetMask(!p.IsModuleEnabled(1));
        InstPanel.SetMask(!p.IsModuleEnabled(2));
        EqPanel.SetMask(!p.IsModuleEnabled(3));
        // 同步算法选择
        CompPanel.SyncAlgorithm(p.FxAlg[0]);
        InstPanel.SyncAlgorithm(p.FxAlg[2]);
        EqPanel.SyncAlgorithm(p.FxAlg[3]);
        RvbPanel.SyncAlgorithm(p.FxAlg[4]);
        GraphEqPanel.ApplyState(p);
        HpfPanelControl.ApplyState(p);
        // 回显各算法参数
        SyncFxParams(CompPanel, 0, p);
        SyncFxParams(InstPanel, 2, p);
        SyncFxParams(EqPanel, 3, p);
        SyncFxParams(RvbPanel, 4, p);
    }

    private static void SyncFxParams(FxAlgorithmPanel panel, int module, Protocol.Models.PresetState p)
    {
        var mod = Protocol.Models.FxCatalog.GetModule(module);
        if (mod == null) return;
        foreach (var alg in mod.Algorithms)
            foreach (var param in alg.Params)
                panel.SyncParam(param.AlgId, p.GetFxPara(module, param.AlgId));
    }

    private void ApplyLocalization()
    {
        _tComp.Text = Loc.Get("Comp");
        _tGraphEq.Text = Loc.Get("GlobalEq");
        _tInst.Text = Loc.Get("Inst");
        _tEq.Text = Loc.Get("Eq");
        _tRvb.Text = Loc.Get("Reverb");
        _tHpf.Text = Loc.Get("Hpf");
    }
}
