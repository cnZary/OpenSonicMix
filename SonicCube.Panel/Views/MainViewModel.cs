using System.ComponentModel;
using System.Runtime.CompilerServices;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Views;

/// <summary>轻量 ViewModel 基类。</summary>
public abstract class VmBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// 主窗口 ViewModel。持有最新设备快照与设置,接收电平。
/// 电平由后台线程只写 volatile 快照,UI 经 CompositionTarget.Rendering 读取,不经 Dispatcher 排队。
/// </summary>
public sealed class MainViewModel : VmBase
{
    // ── 连接/同步状态 ───────────────────────────────────────
    private string _trayState = "connecting";   // connecting / connected / disconnected
    public string TrayState { get => _trayState; set => Set(ref _trayState, value); }

    private bool _deviceConnected;
    public bool DeviceConnected { get => _deviceConnected; set => Set(ref _deviceConnected, value); }

    private bool _synced;
    /// <summary>是否已完成首帧快照同步(此前依赖设备状态的操作保持禁用)。</summary>
    public bool Synced { get => _synced; set => Set(ref _synced, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _alwaysOnTop;
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }

    // ── 设备快照(整包替换,引用不可变语义足够)───────────────
    private DeviceStateDto _state = new();
    public DeviceStateDto State { get => _state; set => Set(ref _state, value); }

    private SettingsDto _settings = new();
    public SettingsDto Settings { get => _settings; set => Set(ref _settings, value); }

    // ── 电平(volatile 最新值,后台写/UI 渲染线程读)──────────
    private volatile float[] _meters = new float[10] { -100, -100, -100, -100, -100, -100, -100, -100, -100, -100 };
    public float[] Meters => _meters;
    public void SetMeters(float[] m) { _meters = m; }

    // ── 效果编辑区当前选中模块(UI 索引,不发协议)────────────
    private int _selectedEffect;
    /// <summary>0=COMP,1=MIC1 EQ,2=HPF(MIC1);3=INST,4=CH2 EQ,5=HPF(CH2);6=RVB。</summary>
    public int SelectedEffect { get => _selectedEffect; set => Set(ref _selectedEffect, value); }

    // ── 重置确认状态 ────────────────────────────────────────
    private bool _resetArmed;
    public bool ResetArmed { get => _resetArmed; set => Set(ref _resetArmed, value); }

    // ── 模拟器标记 ──────────────────────────────────────────
    public bool Simulate { get; init; }
}
