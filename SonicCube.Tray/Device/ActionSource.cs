namespace SonicCube.Tray.Device;

/// <summary>自动/手动动作来源(用于日志区分,见需求第十四节)。</summary>
public enum ActionSource
{
    /// <summary>Windows 登录启动时的初始自动动作。</summary>
    Startup,
    /// <summary>快速连接模式下设备重新连接后的自动动作。</summary>
    DeviceReconnect,
    /// <summary>托盘菜单手动操作。</summary>
    ManualTray,
    /// <summary>Panel 手动操作。</summary>
    ManualPanel,
}

/// <summary>快速开关 ID(托盘六个 + Panel 可复用)。</summary>
public enum QuickSwitch
{
    Phantom48V,
    Efx,
    Monitor,
    OnAir,
    MasterMute,
    Mono,
}

/// <summary>命令执行结果。</summary>
public readonly record struct CommandResult(bool Success, string? ErrorCode, string? ErrorDetail)
{
    public static CommandResult Ok() => new(true, null, null);
    public static CommandResult Fail(string code, string? detail = null) => new(false, code, detail);
}
