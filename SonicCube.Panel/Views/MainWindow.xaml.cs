using System.Windows;
using SonicCube.Panel.Ipc;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Ipc;
using SonicCube.Panel.Simulation;

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
    private readonly SimulatedDevice? _simulator;
    private SettingsDto _settings = new();
    private DeviceStateDto _state = new();
    private PresetState _preset = new();
    private GlobalState _global = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _refreshTask;
    private readonly Dictionary<uint, long> _floatRequests = new();
    private readonly Dictionary<(int Module, int Parameter), long> _fxRequests = new();
    private long _requestGeneration;
    private bool _synced;
    private bool _closing;

    private InputChannelView Mic1 = null!;
    private InputChannelView Line2 = null!;
    private PlaybackChannelView Usb = null!;
    private PlaybackChannelView Otg = null!;

    public MainWindow(TrayClient? client, bool simulate)
    {
        _client = client;
        _simulate = simulate;
        _simulator = simulate ? new SimulatedDevice() : null;
        SetValue(Controls.StateSynchronization.IsLocalProperty, simulate);
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

    // ── 模拟器(本地交互与电平预览)─────────────────────────

    private System.Windows.Threading.DispatcherTimer? _simTimer;
    private System.Diagnostics.Stopwatch? _simClock;

    private void StartSimulator()
    {
        BottomBar.SetStatus(Loc.Get("Simulated"));
        _synced = true;
        _state.Connected = true;
        ApplySimulatedState();
        UpdateUiEnabled();
        _simClock = System.Diagnostics.Stopwatch.StartNew();
        _simTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _simTimer.Tick += OnSimulatorTick;
        IsVisibleChanged += (_, _) => UpdateSimulatorTimer();
        StateChanged += (_, _) => UpdateSimulatorTimer();
        UpdateSimulatorTimer();
    }

    private void ApplySimulatedState()
    {
        _preset = _simulator!.Preset;
        _global = _simulator.Global;
        ApplySnapshotToUi();
        BottomBar.SetStatus(Loc.Get("Simulated"));
    }

    private void OnSimulatorTick(object? sender, EventArgs e) =>
        ApplyMeters(_simulator!.GetMeters(_simClock!.Elapsed));

    private void UpdateSimulatorTimer()
    {
        if (_simTimer == null) return;
        if (!_closing && IsVisible && WindowState != WindowState.Minimized)
        {
            OnSimulatorTick(this, EventArgs.Empty);
            _simTimer.Start();
        }
        else _simTimer.Stop();
    }

    // ── IPC 绑定与状态同步 ──────────────────────────────────

    private void WireIpc()
    {
        _client!.StateUpdated += st => RunOnUi(() => ApplySnapshot(st));
        _client.MetersUpdated += m => ApplyMeters(m);
        _client.ConnectionChanged += conn => RunOnUi(() => OnDeviceConnectionChanged(conn));
        _client.SettingsUpdated += s => RunOnUi(() => ApplySettings(s));
        _client.TrayDisconnected += () => RunOnUi(() =>
        {
            // Tray 退出(托盘菜单退出/异常结束)→ Panel 一并退出;无 Tray 时 Panel 无法操作设备。
            if (_closing) return;
            _closing = true;
            Close();
        });
    }

    private void RunOnUi(Action a)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) a(); else Dispatcher.BeginInvoke(a);
    }

    public async void BeginSync()
    {
        if (_simulate) return;
        BottomBar.SetStatus(Loc.Get("TrayConnecting"));
        try
        {
            string trayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
            if (!await _client!.EnsureConnectedAsync(trayPath, _lifetime.Token))
            {
                if (!_closing) UpdateStatus(false);
                StartRefresh();
                return;
            }
            await _client.SubscribeAndSyncAsync(_lifetime.Token);
            if (_closing) return;
            var settings = await _client.GetSettingsAsync(_lifetime.Token);
            if (_closing) return;
            if (settings != null) ApplySettings(settings);
            if (!_synced) StartRefresh();
        }
        catch (OperationCanceledException) { }
    }

    private void StartRefresh()
    {
        if (_closing || _client == null || _refreshTask is { IsCompleted: false }) return;
        _refreshTask = RefreshWhenReadyAsync();
    }

    private async Task RefreshWhenReadyAsync()
    {
        try
        {
            for (int i = 0; i < 60 && !_closing && !_synced; i++)
            {
                if (!_client!.IsConnected)
                {
                    string trayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
                    if (await _client.EnsureConnectedAsync(trayPath, _lifetime.Token))
                        await _client.SubscribeAsync(_lifetime.Token);
                }
                await _client!.GetStateDirectAsync(_lifetime.Token);
                if (_synced || _closing) return;
                await Task.Delay(200, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnDeviceConnectionChanged(bool connected)
    {
        UpdateStatus(connected);
        if (connected && !_synced) StartRefresh();
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
        if (_closing) return;
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
        _synced = dto.Connected;
        UpdateUiEnabled();
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
        if (!connected)
        {
            _synced = false;
            _floatRequests.Clear();
            _fxRequests.Clear();
            ResetVolumeSynchronization();
        }
        UpdateUiEnabled();
        BottomBar.SetStatus(Loc.Get(_simulate ? "Simulated" : !connected ? "DeviceDisconnected" : _synced ? "DeviceConnected" : "Syncing"));
    }

    private void UpdateUiEnabled()
    {
        bool en = !_closing && (_simulate || (_synced && _state.Connected));
        Mic1.IsEnabled = en; Line2.IsEnabled = en;
        Usb.IsEnabled = en; Otg.IsEnabled = en;
        Master.IsEnabled = en; Fx.IsEnabled = en; Rvb.IsEnabled = en;
        BottomBar.SetDeviceEnabled(en);
    }

    // ── 发送(经 Tray)────────────────────────────────────────

    private void SendInt(uint address, int value)
    {
        if (_simulator != null)
        {
            _simulator.SetInt(address, value);
            ApplySimulatedState();
            return;
        }
        _ = SendSafe(() => _client!.SetIntAsync(address, value));
    }

    private void SendFloat(uint address, float value)
    {
        if (_simulator != null)
        {
            _simulator.SetFloat(address, value);
            ApplySimulatedState();
            return;
        }
        _ = SendFloatWithFeedbackAsync(address, value);
    }

    private void SendFxParam(int module, int algId, float value)
    {
        if (_simulator != null)
        {
            _simulator.SetFxParameter(module, algId, value);
            ApplySimulatedState();
            return;
        }
        _ = SendFxWithFeedbackAsync(module, algId, value);
    }

    private void Toggle(string sw)
    {
        if (_simulator != null)
        {
            _simulator.Toggle(sw);
            ApplySimulatedState();
            return;
        }
        _ = SendSafe(() => _client!.ToggleAsync(sw));
    }

    private async Task<IpcMessage?> SendSafe(Func<Task<IpcMessage?>> op, Func<bool>? isCurrent = null)
    {
        if (_simulate || _client == null || _closing) return null;
        IpcMessage? resp;
        if (!_synced || !_state.Connected)
            resp = new IpcMessage { Success = false, ErrorCode = "not_ready" };
        else
        {
            try { resp = await op(); }
            catch { resp = null; }
        }
        if (_closing || resp?.ErrorCode == "superseded" || isCurrent?.Invoke() == false) return resp;
        if (resp?.Success != true)
        {
            string key = "Err_" + (resp?.ErrorCode ?? "disconnected");
            string error = Loc.Get(key);
            BottomBar.SetStatus($"{Loc.Get("Error")}: {(error == key ? Loc.Get("Err_error") : error)}");
            ApplySnapshotToUi();
        }
        return resp;
    }

    private async Task SendFloatWithFeedbackAsync(uint address, float value)
    {
        long generation = ++_requestGeneration;
        _floatRequests[address] = generation;
        var response = await SendSafe(() => _client!.SetFloatAsync(address, value, _lifetime.Token),
            () => _floatRequests.TryGetValue(address, out var current) && current == generation);
        if (_closing || !_floatRequests.TryGetValue(address, out var latest) || latest != generation) return;
        _floatRequests.Remove(address);
        if (response?.ErrorCode == "superseded") return;
        if (CommandAddress.TypeOf(address) == CommandAddress.VolumePot)
        {
            int channel = CommandAddress.LocationOf(address);
            if (channel < 4)
                CompleteVolumeWrite(channel, value, response?.Success == true);
        }
        else if (response?.Success != true)
        {
            Mic1.FailFloatWrite(address, value);
            Line2.FailFloatWrite(address, value);
        }
    }

    private async Task SendFxWithFeedbackAsync(int module, int parameter, float value)
    {
        var key = (module, parameter);
        long generation = ++_requestGeneration;
        _fxRequests[key] = generation;
        var response = await SendSafe(() => _client!.SetFxParamAsync(module, parameter, value, _lifetime.Token),
            () => _fxRequests.TryGetValue(key, out var current) && current == generation);
        if (_closing || !_fxRequests.TryGetValue(key, out var latest) || latest != generation) return;
        _fxRequests.Remove(key);
        if (response?.Success == true || response?.ErrorCode == "superseded") return;
        Fx.FailFxWrite(module, parameter, value);
        Rvb.FailFxWrite(module, parameter, value);
    }

    private void ResetVolumeSynchronization()
    {
        foreach (var view in new FrameworkElement[] { Mic1, Line2, Usb, Otg })
            ((Controls.VolumeFaderControl)view.FindName("Fader")).ResetSynchronization();
    }

    private void CompleteVolumeWrite(int channel, float value, bool success)
    {
        var view = new FrameworkElement[] { Mic1, Line2, Usb, Otg }[channel];
        var fader = (Controls.VolumeFaderControl)view.FindName("Fader");
        fader.CompletePendingWrite(value, success);
        fader.SetDbSilently(_preset.VolPot[channel]);
    }

    // ── 重置(当前选中模块 / 完全重置整机,均需确认)──────────────

    private void OnFullResetRequested()
    {
        if (_simulator != null)
        {
            _simulator.ResetAll();
            ApplySimulatedState();
            return;
        }
        var r = MessageBox.Show(this, Loc.Get("ResetConfirm"), Loc.Get("ResetAll"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _ = SendSafe(() => _client!.SetIntAsync(CommandAddress.CommandReset, 0));
    }

    private void OnResetRequested(int uiIndex)
    {
        // uiIndex → (module, 本地化名称键):0=COMP 1=MIC1 EQ 3=INST 4=CH2 EQ;2/5=HPF
        int module = uiIndex switch { 0 => 0, 1 => 1, 3 => 2, 4 => 3, _ => -1 };
        if (_simulator != null)
        {
            if (module >= 0) _simulator.ResetModule(module);
            else
            {
                int channel = uiIndex == 2 ? 0 : 1;
                _simulator.SetInt(CommandAddress.WithLocation(CommandAddress.HpfSwitch, channel), 0);
                _simulator.SetInt(CommandAddress.WithLocation(CommandAddress.LowCut, channel), 0);
            }
            ApplySimulatedState();
            return;
        }
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
        var w = new SettingsWindow(_client, _settings) { Owner = this };
        w.SettingsSaved += s =>
        {
            ApplySettings(s);
            if (_simulate) BottomBar.SetStatus(Loc.Get("Simulated"));
        };
        w.ShowDialog();
    }

    private void ApplyLocalization()
    {
        Title = Loc.Get("AppTitle");
        if (BottomBar != null) UpdateStatus(_state.Connected);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true; // 自己释放连接也会触发 TrayDisconnected,防止重入
        _lifetime.Cancel();
        Loc.LanguageChanged -= ApplyLocalization;
        base.OnClosed(e);
        _simTimer?.Stop();
        if (_simTimer != null) _simTimer.Tick -= OnSimulatorTick;
        // 关闭即退出进程;取消订阅并释放 IPC(Tray 据此释放连接保留条件)。
        if (_client != null)
        {
            _ = _client.SendUnsubscribeQuiet();
            _client.Dispose();
        }
        Application.Current.Shutdown();
    }
}
