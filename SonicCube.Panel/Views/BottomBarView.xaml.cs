using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 底部全局控制条:状态灯 + EFX/MONITOR/ONAIR + 右端菜单。
/// 状态文本反映 Tray 连接与设备状态(真实,不显示虚假 Connected)。
/// </summary>
public partial class BottomBarView : UserControl
{
    private bool _suppress;

    public event Action<string>? ToggleRequested;
    public event Action? MenuRequested;

    public BottomBarView()
    {
        InitializeComponent();
        BtnEfx.Click += (_, _) => { if (!_suppress) ToggleRequested?.Invoke("Efx"); };
        BtnMonitor.Click += (_, _) => { if (!_suppress) ToggleRequested?.Invoke("Monitor"); };
        BtnOnAir.Click += (_, _) => { if (!_suppress) ToggleRequested?.Invoke("OnAir"); };
        MenuBtn.Click += (_, _) => MenuRequested?.Invoke();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    public void ApplyState(GlobalState g)
    {
        _suppress = true;
        BtnEfx.IsChecked = g.Efx == 1;
        BtnMonitor.IsChecked = g.Monitor == 1;
        BtnOnAir.IsChecked = g.OnAir == 1;
        _suppress = false;
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
        string brushKey = "BrushOn";
        if (text == Loc.Get("DeviceDisconnected") || text.StartsWith(Loc.Get("Error"), StringComparison.OrdinalIgnoreCase))
            brushKey = "BrushError";
        else if (text == Loc.Get("TrayConnecting") || text.StartsWith(Loc.Get("Syncing"), StringComparison.OrdinalIgnoreCase) || text == Loc.Get("Simulated"))
            brushKey = "BrushAmber";
        if (TryFindResource(brushKey) is Brush b)
            StatusDot.Fill = b;
    }

    private void ApplyLocalization()
    {
        BtnEfx.Content = Loc.Get("Efx");
        BtnMonitor.Content = Loc.Get("Monitor");
        BtnOnAir.Content = Loc.Get("OnAir");
        SettingsLabel.Text = Loc.Get("Settings");
        System.Windows.Automation.AutomationProperties.SetName(MenuBtn, Loc.Get("Settings"));
    }
}
