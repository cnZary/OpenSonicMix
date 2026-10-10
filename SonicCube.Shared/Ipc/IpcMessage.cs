using System.Text.Json.Serialization;

namespace SonicCube.Shared.Ipc;

/// <summary>
/// IPC 消息(JSON Lines,UTF-8,每行一个完整 JSON 对象,见需求第九节)。
/// 通过 requestId 对应请求与响应;错误信息通过错误码本地化。
/// </summary>
public sealed class IpcMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }

    // set_parameter / toggle_switch
    [JsonPropertyName("address")] public uint? Address { get; set; }
    [JsonPropertyName("intValue")] public int? IntValue { get; set; }
    [JsonPropertyName("floatValue")] public float? FloatValue { get; set; }
    [JsonPropertyName("module")] public int? Module { get; set; }
    [JsonPropertyName("paramIndex")] public int? ParamIndex { get; set; }
    [JsonPropertyName("switch")] public string? Switch { get; set; }

    // set_work_mode / update_settings
    [JsonPropertyName("workMode")] public string? WorkMode { get; set; }
    [JsonPropertyName("settings")] public SettingsDto? Settings { get; set; }

    // 响应载荷
    [JsonPropertyName("success")] public bool? Success { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonPropertyName("errorDetail")] public string? ErrorDetail { get; set; }
    [JsonPropertyName("version")] public long? Version { get; set; }
    [JsonPropertyName("state")] public DeviceStateDto? State { get; set; }
    [JsonPropertyName("meters")] public float[]? Meters { get; set; }
    [JsonPropertyName("deviceStatus")] public string? DeviceStatus { get; set; }
}

public sealed class SettingsDto
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("startWithWindows")] public bool? StartWithWindows { get; set; }
    [JsonPropertyName("workMode")] public string? WorkMode { get; set; }
    [JsonPropertyName("idleDisconnectSeconds")] public int? IdleDisconnectSeconds { get; set; }
    [JsonPropertyName("enable48V")] public bool? Enable48V { get; set; }
    [JsonPropertyName("enableEFX")] public bool? EnableEFX { get; set; }
    [JsonPropertyName("reapplyOnReconnect")] public bool? ReapplyOnReconnect { get; set; }
    [JsonPropertyName("phantomPowerConsent")] public bool? PhantomPowerConsent { get; set; }
    [JsonPropertyName("alwaysOnTop")] public bool? AlwaysOnTop { get; set; }
}

/// <summary>设备全量状态快照(供 Panel 启动同步)。</summary>
public sealed class DeviceStateDto
{
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    // 全局
    [JsonPropertyName("phantom48V")] public int Phantom48V { get; set; }
    [JsonPropertyName("efx")] public int Efx { get; set; }
    [JsonPropertyName("reverbOn")] public int ReverbOn { get; set; }
    [JsonPropertyName("monitor")] public int Monitor { get; set; }
    [JsonPropertyName("onAir")] public int OnAir { get; set; }
    [JsonPropertyName("masterMute")] public int MasterMute { get; set; }
    // 预设
    [JsonPropertyName("inverseSw")] public int[] InverseSw { get; set; } = new int[2];
    [JsonPropertyName("hpfSw")] public int[] HpfSw { get; set; } = new int[2];
    [JsonPropertyName("lowCut")] public int[] LowCut { get; set; } = new int[2];
    [JsonPropertyName("loopbackSw")] public int[] LoopbackSw { get; set; } = new int[2];
    [JsonPropertyName("dspRecSw")] public int DspRecSw { get; set; }
    [JsonPropertyName("fxModuleEn")] public int FxModuleEn { get; set; }
    [JsonPropertyName("fxAlg")] public int[] FxAlg { get; set; } = new int[6];
    [JsonPropertyName("fxParaVal")] public float[] FxParaVal { get; set; } = new float[96];
    [JsonPropertyName("muteSw")] public int[] MuteSw { get; set; } = new int[4];
    [JsonPropertyName("soloSw")] public int[] SoloSw { get; set; } = new int[2];
    [JsonPropertyName("volPot")] public float[] VolPot { get; set; } = new float[4];
    [JsonPropertyName("phase")] public float[] Phase { get; set; } = new float[2];
    [JsonPropertyName("send")] public float[] Send { get; set; } = new float[2];
    [JsonPropertyName("monoSw")] public int MonoSw { get; set; }
}

/// <summary>消息类型常量。</summary>
public static class IpcMessageType
{
    public const string GetState = "get_state";
    public const string GetSettings = "get_settings";
    public const string UpdateSettings = "update_settings";
    public const string SetParameter = "set_parameter";
    public const string ToggleSwitch = "toggle_switch";
    public const string SetWorkMode = "set_work_mode";
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string DeviceStatusChanged = "device_status_changed";
    public const string StateChanged = "state_changed";
    public const string MeterUpdate = "meter_update";
    public const string CommandResult = "command_result";
    public const string Error = "error";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(DeviceStateDto))]
public sealed partial class IpcJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
