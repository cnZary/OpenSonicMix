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
        Fx.FullResetRequested += OnFullResetRequested;        // 通道按钮联动效果区页签
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
        _t0 = System.Diagnostics.Stopwatch.StartNew();
        if (_simulate) return;
        UpdateStatus(false);
        BottomBar.SetStatus(Loc.Get("TrayConnecting"));

        // Tray 未运行时尽早拉起并等它就绪:App.OnStartup 已并发发起同一连接(信号量共享),
        // 这里等连接建立后再首轮同步,避免空跑一轮后落入粗粒度重试循环。
        string trayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
        bool conn = await _client!.EnsureConnectedAsync(trayPath);

        var st = conn ? await _client.SubscribeAndSyncAsync() : null;
        var settings = conn ? await _client.GetSettingsAsync() : null;
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
            if (_synced && _t0 != null)
            {
                var el = _t0.ElapsedMilliseconds;
                _t0 = null;
                BottomBar.SetStatus($"{Loc.Get("Syncing")} {el} ms");
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) => { timer.Stop(); UpdateStatus(_state.Connected); };
                timer.Start();
            }
        });

        // Panel 拉起 Tray 的竞态:Panel 可能在 Tray IPC/设备握手完成前连接,get_state 拿到空/超时。
        // 兜底:快照缺失或版本为 0 时,持续重试拉取直到拿到真实快照。
        if (st == null || _client!.StateVersion == 0)
        {
            _ = RefresWhenReadyAsync();
        }
    }

    private async Task RefresWhenReadyAsync()
    {
        // 设备握手可能要几秒;多轮尝试,每轮先等 Tray 连接可用再(重新)订阅并拉快照。
        for (int i = 0; i < 60; i++)
        {
            if (_client == null) return;
            if (!_client.IsConnected)
            {
                string trayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
                await _client.EnsureConnectedAsync(trayPath);
                // 重连后必须重新订阅(冷启动时首次 subscribe 可能已超时,导致电平广播被抑制)。
                await _client.SubscribeAsync();
            }
            else
            {
                // 已连接则立即拉一次快照(首轮无等待),版本为 0 说明设备握手未完成,下轮再试。
                var st = await _client.GetStateDirectAsync();
                if (st != null && _client.StateVersion > 0)
                {
                    RunOnUi(() => ApplySnapshot(st));
                    RunOnUi(() => { _synced = true; UpdateUiEnabled(); UpdateStatus(st.Connected); });
                    return;
                }
            }
            await Task.Delay(200);
        }
    }

    private System.Diagnostics.Stopwatch? _t0;

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

    // ── 重置(当前选中模块 / 完全重置整机,均需确认)──────────────

    private void OnFullResetRequested()
    {
        if (_simulate) return;
        var r = MessageBox.Show(this, Loc.Get("ResetConfirm"), Loc.Get("ResetAll"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _ = SendSafe(() => _client!.SetIntAsync(CommandAddress.CommandReset, 0));
    }

    private void OnResetRequested(int uiIndex)
    {
        if (_simulate) return;
        // uiIndex → (module, 本地化名称键):0=COMP 1=MIC1 EQ 3=INST 4=CH2 EQ;2/5=HPF
        int module = uiIndex switch { 0 => 0, 1 => 1, 3 => 2, 4 => 3, _ => -1 };
        string nameKey = uiIndex switch { 0 => "Comp", 1 => "Eq", 3 => "Inst", 4 => "Eq", _ => "Hpf" };
        var r = MessageBox.Show(this, Loc.Format("ResetModuleConfirm", Loc.Get(nameKey)), Loc.Get("Reset"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        if (module >= 0)
        {
            _ = SendSafe(() => _client!.ResetModuleAsync(module));
        }
        else
        {
            // HPF 页:无算法参数;重置 = 关该通道 HpfSwitch + LowCut 回 75Hz。
            int ch = uiIndex == 2 ? 0 : 1;
            SendInt(CommandAddress.WithLocation(CommandAddress.HpfSwitch, ch), 0);
            SendInt(CommandAddress.WithLocation(CommandAddress.LowCut, ch), 0);
        }
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
