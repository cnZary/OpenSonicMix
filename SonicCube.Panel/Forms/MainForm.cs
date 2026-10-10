using SonicCube.Panel.Controls;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Forms;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// SonicCubePanel 主窗体。
/// 布局:顶部状态栏 + 总线;左侧输入通道;中部回放通道;右侧/下部效果区。
/// 所有设备操作经 IPC 转交 Tray;快照 + 订阅同步;电平限频分发。
/// </summary>
public sealed class MainForm : Form
{
    private readonly SonicCube.Panel.Ipc.TrayClient _client;

    private InputChannelPanel _mic1 = null!;
    private InputChannelPanel _line2 = null!;
    private PlaybackChannelPanel _usb = null!;
    private PlaybackChannelPanel _otg = null!;
    private BusPanel _bus = null!;
    private FxSectionPanel _fx = null!;
    private Label _statusLabel = null!;
    private Button _settingsBtn = null!;
    private SettingsDto _settings = new();

    // 用于电平分发
    private float[]? _meters;

    public MainForm(SonicCube.Panel.Ipc.TrayClient client)
    {
        _client = client;
        BuildUi();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
        WireIpc();
    }

    private void BuildUi()
    {
        Text = "SonicCubePanel";
        Width = 1180; Height = 760;
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.FromArgb(220, 220, 225);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);

        // 顶部:状态 + 设置
        var topBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(24, 24, 28) };
        _statusLabel = new Label { Left = 12, Top = 10, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _settingsBtn = new Button { Text = "⚙", Width = 36, Height = 30, Top = 5, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 50, 56), ForeColor = Color.White, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _settingsBtn.FlatAppearance.BorderSize = 0;
        topBar.Controls.Add(_statusLabel);
        topBar.Controls.Add(_settingsBtn);
        _settingsBtn.Left = topBar.Width - 48;
        topBar.Resize += (_, _) => _settingsBtn.Left = topBar.Width - 48;

        // 总线面板
        _bus = new BusPanel { Dock = DockStyle.Top, Height = 92 };

        // 主区:左侧输入,中间回放,右侧效果
        var main = new Panel { Dock = DockStyle.Fill };

        var channels = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 560, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        _mic1 = new InputChannelPanel(0) { Width = 200, Height = 560, Margin = new Padding(6) };
        _line2 = new InputChannelPanel(1) { Width = 200, Height = 560, Margin = new Padding(6) };
        var pbCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(6) };
        _usb = new PlaybackChannelPanel(0) { Width = 150, Height = 340, Margin = new Padding(0, 0, 0, 6) };
        _otg = new PlaybackChannelPanel(1) { Width = 150, Height = 340, Margin = new Padding(0) };
        pbCol.Controls.Add(_usb);
        pbCol.Controls.Add(_otg);
        channels.Controls.Add(_mic1);
        channels.Controls.Add(_line2);
        channels.Controls.Add(pbCol);

        _fx = new FxSectionPanel { Dock = DockStyle.Fill };

        main.Controls.Add(_fx);
        main.Controls.Add(channels);

        Controls.Add(main);
        Controls.Add(_bus);
        Controls.Add(topBar);

        WireChannelEvents();
        _settingsBtn.Click += (_, _) => OpenSettings();
    }

    private void WireChannelEvents()
    {
        // int 写入
        _mic1.SetIntRequested += SendInt; _line2.SetIntRequested += SendInt;
        _usb.SetIntRequested += SendInt; _otg.SetIntRequested += SendInt;
        _bus.SetIntRequested += SendInt;
        _fx.HpfPanelControl.SetIntRequested += SendInt;
        // float 写入
        _mic1.SetFloatRequested += SendFloat; _line2.SetFloatRequested += SendFloat;
        _usb.SetFloatRequested += SendFloat; _otg.SetFloatRequested += SendFloat;
        // 48V toggle
        _mic1.Toggle48VRequested += () => _ = _client.ToggleAsync("Phantom48V");
        // 总线 toggle
        _bus.ToggleRequested += sw => _ = _client.ToggleAsync(sw);
        // 效果区
        _fx.CompPanel.AlgorithmChangeRequested += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, 0), fxId);
        _fx.InstPanel.AlgorithmChangeRequested += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, 2), fxId);
        _fx.EqPanel.AlgorithmChangeRequested += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, 3), fxId);
        _fx.RvbPanel.AlgorithmChangeRequested += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, 4), fxId);
        _fx.CompPanel.FxParamRequested += SendFxParam;
        _fx.InstPanel.FxParamRequested += SendFxParam;
        _fx.EqPanel.FxParamRequested += SendFxParam;
        _fx.RvbPanel.FxParamRequested += SendFxParam;
        _fx.GraphEqPanel.FxParamRequested += SendFxParam;
        // 效果页切换
        _mic1.ShowEffectRequested += i => _fx.ShowEffect(i);
        _line2.ShowEffectRequested += i => _fx.ShowEffect(i);
    }

    private void SendInt(uint address, int value) => _ = SendSafe(() => _client.SetIntAsync(address, value));
    private void SendFloat(uint address, float value) => _ = SendSafe(() => _client.SetFloatAsync(address, value));
    private void SendFxParam(int module, int algId, float value) => _ = SendSafe(() => _client.SetFxParamAsync(module, algId, value));

    private async Task SendSafe(Func<Task<IpcMessage?>> op)
    {
        try
        {
            var resp = await op();
            if (resp != null && resp.Success == false)
                ShowError(resp.ErrorCode);
        }
        catch { }
    }

    private void ShowError(string? code)
    {
        if (code == null) return;
        string msg = Loc.Get("Err_" + code);
        if (InvokeRequired) BeginInvoke(() => _statusLabel.Text = $"{Loc.Get("Error")}: {msg}");
        else _statusLabel.Text = $"{Loc.Get("Error")}: {msg}";
    }

    // ── IPC 绑定与状态同步 ──────────────────────────────────

    private void WireIpc()
    {
        _client.StateUpdated += st => RunOnUi(() => ApplySnapshot(st));
        _client.MetersUpdated += m => RunOnUi(() => ApplyMeters(m));
        _client.ConnectionChanged += conn => RunOnUi(() => UpdateStatus(conn));
        _client.SettingsUpdated += s => RunOnUi(() => _settings = s);
        _client.TrayDisconnected += () => RunOnUi(() => _statusLabel.Text = Loc.Get("TrayNotRunning"));
    }

    private void RunOnUi(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    /// <summary>将 DTO 转为协议模型并分发到各面板。</summary>
    private void ApplySnapshot(DeviceStateDto dto)
    {
        var p = new PresetState();
        Array.Copy(dto.InverseSw, p.InverseSw, 2);
        Array.Copy(dto.HpfSw, p.HpfSw, 2);
        Array.Copy(dto.LowCut, p.LowCut, 2);
        Array.Copy(dto.LoopbackSw, p.LoopbackSw, 2);
        p.DspRecSw = dto.DspRecSw; p.FxModuleEn = dto.FxModuleEn;
        Array.Copy(dto.FxAlg, p.FxAlg, 6);
        Array.Copy(dto.FxParaVal, p.FxParaVal, Math.Min(96, dto.FxParaVal.Length));
        Array.Copy(dto.MuteSw, p.MuteSw, 4);
        Array.Copy(dto.SoloSw, p.SoloSw, 2);
        Array.Copy(dto.VolPot, p.VolPot, 4);
        Array.Copy(dto.Phase, p.Phase, 2);
        Array.Copy(dto.Send, p.Send, 2);
        p.MonoSw = dto.MonoSw;

        var g = new GlobalState
        {
            Phantom48V = dto.Phantom48V, Efx = dto.Efx, ReverbOn = dto.ReverbOn,
            Monitor = dto.Monitor, OnAir = dto.OnAir, MasterMute = dto.MasterMute,
        };

        _mic1.ApplyState(p, g, _meters);
        _line2.ApplyState(p, g, _meters);
        _usb.ApplyState(p, _meters);
        _otg.ApplyState(p, _meters);
        _bus.ApplyState(p, g, _meters);
        _fx.ApplyState(p);
        UpdateStatus(dto.Connected);
    }

    private void ApplyMeters(float[] m)
    {
        _meters = m;
        _mic1.SetMeter(m[0], m[1]);
        _line2.SetMeter(m[2], m[3]);
        _usb.SetMeter(m[4], m[5]);
        _otg.SetMeter(m[6], m[7]);
        _bus.SetMasterMeter(m[8], m[9]);
    }

    private void UpdateStatus(bool connected)
    {
        _statusLabel.Text = connected ? Loc.Get("DeviceConnected") : Loc.Get("DeviceDisconnected");
        _statusLabel.ForeColor = connected ? Color.FromArgb(90, 210, 120) : Color.FromArgb(220, 120, 100);
    }

    // ── 设置 ────────────────────────────────────────────────

    private void OpenSettings()
    {
        using var form = new SettingsForm(_client, _settings);
        form.SettingsSaved += s =>
        {
            _settings = s;
            TopMost = s.AlwaysOnTop ?? false;
        };
        form.ShowDialog(this);
    }

    public async Task InitializeAsync()
    {
        // 订阅 + 快照同步。
        var st = await _client.SubscribeAndSyncAsync();
        var settings = await _client.GetSettingsAsync();
        if (settings != null)
        {
            _settings = settings;
            RunOnUi(() =>
            {
                TopMost = settings.AlwaysOnTop ?? false;
                Loc.Apply(settings.Language ?? "auto");
            });
        }
        if (st != null) RunOnUi(() => ApplySnapshot(st));
    }

    private void ApplyLocalization()
    {
        Text = Loc.Get("AppTitle");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // 关闭即退出进程(见需求 10.1);取消订阅并释放 IPC。
        _ = _client.UpdateSettingsAsync(new SettingsDto()); // no-op 保持管道整洁
        _client.Dispose();
    }
}
