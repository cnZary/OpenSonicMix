using System.Windows;
using SonicCube.Panel.Ipc;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Views;

/// <summary>
/// 主窗口。布局接近官方 Sonic Mix:左两条输入通道、中央上效果编辑/下回放+混响、右主输出、底部全局条。
/// 启动顺序:先显示窗口 → 后台连接 Tray → 订阅并同步快照 → 启用设备操作。
/// 所有设备写入经 TrayClient;电平经 volatile 快照 + MeterBar 渲染线程读取。
/// </summary>
public partial class MainWindow : Window
{
    private readonly TrayClient? _client;
    private readonly bool _simulate;
    private SettingsDto _settings = new();
    private DeviceStateDto _state = new();
    private PresetState _preset = new();
    private GlobalState _global = new();
    private long _stateVersion;
    private bool _synced;

    private InputChannelView Mic1 = null!;
    private InputChannelView Line2 = null!;
    private PlaybackChannelView Usb = null!;
    private PlaybackChannelView Otg = null!;

    public MainWindow(TrayClient? client, bool simulate)
    {
        _client = client;
        _simulate = simulate;
        InitializeComponent();
        Mic1 = new InputChannelView(0);
        Line2 = new InputChannelView(1);
        Usb = new PlaybackChannelView(0);
        Otg = new PlaybackChannelView(1);
        Mic1Host.Child = Mic1;
        Line2Host.Child = Line2;
        UsbHost.Child = Usb;
        OtgHost.Child = Otg;
        BuildUi();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
        if (_client != null) WireIpc();
        if (_simulate) StartSimulator();
        UpdateUiEnabled();
    }

    private void BuildUi()
    {
        // int 写入
        Mic1.SetIntRequested += SendInt; Line2.SetIntRequested += SendInt;
        Usb.SetIntRequested += SendInt; Otg.SetIntRequested += SendInt;
        Master.SetIntRequested += SendInt;
        Fx.SetIntRequested += SendInt;
        // float 写入
        Mic1.SetFloatRequested += SendFloat; Line2.SetFloatRequested += SendFloat;
        Usb.SetFloatRequested += SendFloat; Otg.SetFloatRequested += SendFloat;
        // toggle
        Mic1.ToggleRequested += Toggle; Master.ToggleRequested += Toggle; BottomBar.ToggleRequested += Toggle;
        // 效果区
        Fx.CompAlg += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, FxCatalog.ModuleComp), fxId);
        Fx.InstAlg += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, FxCatalog.ModuleInst), fxId);
        Fx.EqAlg += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, FxCatalog.ModuleEq), fxId);
        Fx.FxParamRequested += SendFxParam;
        Rvb.AlgorithmChangeRequested += fxId => SendInt(CommandAddress.WithLocation(CommandAddress.FxAlgorithm, FxCatalog.ModuleRvb), fxId);
        Rvb.FxParamRequested += SendFxParam;
        Fx.ResetRequested += OnResetRequested;
        // 通道按钮联动效果区页签
        Mic1.ShowEffectRequested += i => Fx.ShowEffect(i);
        Line2.ShowEffectRequested += i => Fx.ShowEffect(i);
        // 底部菜单
        BottomBar.MenuRequested += OpenSettings;
    }

    // ── 模拟器(仅视觉原型,不发真实设备写入)─────────────────

    private System.Threading.Timer? _simTimer;

    private void StartSimulator()
    {
        BottomBar.SetStatus(Loc.Get("Simulated"));
        _synced = true;
        _state.Connected = true;
        _global = new GlobalState { Efx = 1, Monitor = 1 };
        _preset.MonoSw = 0;
        _preset.DspRecSw = 1;
        _preset.FxModuleEn = 0b011111;
        _preset.VolPot[0] = -8; _preset.VolPot[1] = -12; _preset.VolPot[2] = -5; _preset.VolPot[3] = -5;
        _preset.Send[0] = 0.25f; _preset.Send[1] = 0.1f;
        _preset.Phase[0] = -50; _preset.Phase[1] = 50;
        _preset.FxAlg[0] = 1; _preset.FxAlg[2] = 0x07000027; _preset.FxAlg[3] = 0x01000035; _preset.FxAlg[4] = 0x0C000000;
        ApplySnapshotToUi();
        UpdateUiEnabled();
        // 模拟电平推送
        var rnd = new Random();
        _simTimer = new System.Threading.Timer(_ =>
        {
            var m = new float[10];
            for (int i = 0; i < 10; i++)
            {
                double baseDb = i < 8 ? -18 + rnd.NextDouble() * 14 : -12 + rnd.NextDouble() * 10;
                m[i] = (float)baseDb;
            }
            Dispatcher.InvokeAsync(() => ApplyMeters(m));
        }, null, 60, 60);
    }

    // ── IPC 绑定与状态同步 ──────────────────────────────────

    private void WireIpc()
    {
        _client!.StateUpdated += st => RunOnUi(() => ApplySnapshot(st));
        _client.MetersUpdated += m => ApplyMeters(m);
        _client.ConnectionChanged += conn => RunOnUi(() => UpdateStatus(conn));
        _client.SettingsUpdated += s => RunOnUi(() => ApplySettings(s));
        _client.TrayDisconnected += () => RunOnUi(() => { UpdateStatus(false); BottomBar.SetStatus(Loc.Get("TrayNotRunning")); });
    }

    private void RunOnUi(Action a)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) a(); else Dispatcher.BeginInvoke(a);
    }

    public async void BeginSync()
    {
        if (_simulate) return;
        UpdateStatus(false);
        BottomBar.SetStatus(Loc.Get("TrayConnecting"));
        var st = await _client!.SubscribeAndSyncAsync();
        var settings = await _client.GetSettingsAsync();
        if (settings != null)
        {
            _settings = settings;
            RunOnUi(() => ApplySettings(settings));
        }
        if (st != null) RunOnUi(() => ApplySnapshot(st));
        _synced = st != null;
        RunOnUi(() =>
        {
            UpdateUiEnabled();
            if (st != null) UpdateStatus(st.Connected);
        });
    }

    private void ApplySettings(SettingsDto s)
    {
        _settings = s;
        Topmost = s.AlwaysOnTop ?? false;
        Loc.Apply(s.Language ?? "auto");
    }

    private void ApplySnapshot(DeviceStateDto dto)
    {
        _state = dto;
        _stateVersion = _client?.StateVersion ?? _stateVersion;
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
        _preset = p;
        _global = new GlobalState
        {
            Phantom48V = dto.Phantom48V, Efx = dto.Efx, ReverbOn = dto.ReverbOn,
            Monitor = dto.Monitor, OnAir = dto.OnAir, MasterMute = dto.MasterMute,
        };
        ApplySnapshotToUi();
        UpdateStatus(dto.Connected);
    }

    private void ApplySnapshotToUi()
    {
        Mic1.ApplyState(_preset, _global);
        Line2.ApplyState(_preset, _global);
        Usb.ApplyState(_preset);
        Otg.ApplyState(_preset);
        Master.ApplyState(_preset, _global);
        Fx.ApplyState(_preset);
        Rvb.ApplyState(_preset);
        Rvb.SetMask(_global.ReverbOn != 1);
        BottomBar.ApplyState(_global);
    }

    private void ApplyMeters(float[] m)
    {
        if (m.Length < 10) return;
        Mic1.SetMeter(m[0], m[1]);
        Line2.SetMeter(m[2], m[3]);
        Usb.SetMeter(m[4], m[5]);
        Otg.SetMeter(m[6], m[7]);
        Master.SetMeter(m[8], m[9]);
    }

    private void UpdateStatus(bool connected)
    {
        _state.Connected = connected;
        BottomBar.SetStatus(connected ? Loc.Get("DeviceConnected") : Loc.Get("DeviceDisconnected"));
    }

    private void UpdateUiEnabled()
    {
        bool en = _synced || _simulate;
        Mic1.IsEnabled = en; Line2.IsEnabled = en;
        Usb.IsEnabled = en; Otg.IsEnabled = en;
        Master.IsEnabled = en; Fx.IsEnabled = en; Rvb.IsEnabled = en;
    }

    // ── 发送(经 Tray)────────────────────────────────────────

    private void SendInt(uint address, int value) => _ = SendSafe(() => _client!.SetIntAsync(address, value));
    private void SendFloat(uint address, float value) => _ = SendSafe(() => _client!.SetFloatAsync(address, value));
    private void SendFxParam(int module, int algId, float value) => _ = SendSafe(() => _client!.SetFxParamAsync(module, algId, value));
    private void Toggle(string sw) => _ = SendSafe(() => _client!.ToggleAsync(sw));

    private async Task SendSafe(Func<Task<IpcMessage?>> op)
    {
        if (_simulate || _client == null) return;
        try
        {
            var resp = await op();
            if (resp != null && resp.Success == false)
                BottomBar.SetStatus($"{Loc.Get("Error")}: {Loc.Get("Err_" + resp.ErrorCode)}");
        }
        catch { }
    }

    // ── 重置(整台设备,需确认)────────────────────────────────

    private void OnResetRequested()
    {
        if (_simulate) return;
        var r = MessageBox.Show(this, Loc.Get("ResetConfirm"), Loc.Get("Reset"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _ = SendSafe(() => _client!.SetIntAsync(CommandAddress.CommandReset, 0));
    }

    // ── 设置 ────────────────────────────────────────────────

    private void OpenSettings()
    {
        if (_simulate)
        {
            MessageBox.Show(this, Loc.Get("Simulated"), "Settings", MessageBoxButton.OK);
            return;
        }
        var w = new SettingsWindow(_client!, _settings) { Owner = this };
        w.SettingsSaved += s =>
        {
            _settings = s;
            Topmost = s.AlwaysOnTop ?? false;
        };
        w.ShowDialog();
    }

    private void ApplyLocalization()
    {
        Title = Loc.Get("AppTitle");
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _simTimer?.Dispose();
        // 关闭即退出进程;取消订阅并释放 IPC(Tray 据此释放连接保留条件)。
        if (_client != null)
        {
            _ = _client.SendUnsubscribeQuiet();
            _client.Dispose();
        }
        Application.Current.Shutdown();
    }
}
