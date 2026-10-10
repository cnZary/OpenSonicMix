namespace SonicCube.Tray.Localization;

/// <summary>
/// Tray 轻量本地化(NativeAOT 兼容,避免 resx 卫星程序集的 AOT/裁剪问题)。
/// Panel 使用 resx;Tray 菜单文案较少,用字典实现中英双语。
/// </summary>
public static class TrayStrings
{
    public enum Key
    {
        AppTitle,
        Switch48V, SwitchEFX, SwitchMonitor, SwitchOnAir, SwitchMasterMute, SwitchMono,
        WorkMode, ModeLowResource, ModeFastConnection,
        OpenPanel, Exit,
        ErrOpenFailed, ErrOpenFailedHint, ErrToggleFailed, ErrTimeout, ErrNotConnected,
        StartupActionsDone, ReconnectActionsDone,
    }

    private static readonly Dictionary<Key, (string Zh, string En)> Table = new()
    {
        [Key.AppTitle] = ("SonicCubePanel", "SonicCubePanel"),
        [Key.Switch48V] = ("48V", "48V"),
        [Key.SwitchEFX] = ("EFX", "EFX"),
        [Key.SwitchMonitor] = ("MONITOR", "MONITOR"),
        [Key.SwitchOnAir] = ("ONAIR", "ONAIR"),
        [Key.SwitchMasterMute] = ("总静音", "Master Mute"),
        [Key.SwitchMono] = ("MONO", "MONO"),
        [Key.WorkMode] = ("工作模式", "Work Mode"),
        [Key.ModeLowResource] = ("低占用模式", "Low Resource Mode"),
        [Key.ModeFastConnection] = ("快速连接模式", "Fast Connection Mode"),
        [Key.OpenPanel] = ("打开完整面板", "Open Full Panel"),
        [Key.Exit] = ("退出", "Exit"),
        [Key.ErrOpenFailed] = ("无法打开 MIDI 设备", "Cannot open MIDI device"),
        [Key.ErrOpenFailedHint] = ("端口可能被其它程序(如 Sonic Mix)占用,请检查后重试", "The port may be in use by another program (e.g. Sonic Mix)."),
        [Key.ErrToggleFailed] = ("开关切换失败", "Switch toggle failed"),
        [Key.ErrTimeout] = ("设备响应超时", "Device response timeout"),
        [Key.ErrNotConnected] = ("设备未连接", "Device not connected"),
        [Key.StartupActionsDone] = ("启动自动动作已执行", "Startup actions applied"),
        [Key.ReconnectActionsDone] = ("重连自动动作已执行", "Reconnect actions applied"),
    };

    /// <summary>当前界面语言(zh / en)。</summary>
    public static string Language { get; set; } = DetectSystem();

    public static string DetectSystem()
    {
        try
        {
            var name = System.Globalization.CultureInfo.CurrentUICulture.Name;
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        }
        catch { return "en"; }
    }

    /// <summary>根据配置语言解析实际语言("auto" 跟随系统)。</summary>
    public static string Resolve(string configured)
    {
        if (string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)) return DetectSystem();
        if (configured.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
        return "en";
    }

    public static string Get(Key key)
    {
        var (zh, en) = Table[key];
        return Language == "zh" ? zh : en;
    }
}
