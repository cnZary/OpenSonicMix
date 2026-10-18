using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonicCube.Tray.Settings;

/// <summary>MIDI 工作模式。</summary>
public enum WorkMode
{
    /// <summary>低占用模式。</summary>
    LowResource,
    /// <summary>快速连接模式(默认,保持连接)。</summary>
    FastConnection,
}

/// <summary>settings.json 配置模型(见需求第十一节)。</summary>
public sealed class AppSettings
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = false;
    [JsonPropertyName("workMode")] public string WorkModeValue { get; set; } = "fastConnection";
    [JsonPropertyName("idleDisconnectSeconds")] public int IdleDisconnectSeconds { get; set; } = 60;
    [JsonPropertyName("startup")] public StartupSettings Startup { get; set; } = new();
    [JsonPropertyName("panel")] public PanelSettings Panel { get; set; } = new();
    [JsonPropertyName("logging")] public LoggingSettings Logging { get; set; } = new();

    [JsonIgnore]
    public WorkMode Mode
    {
        get => string.Equals(WorkModeValue, "fastConnection", StringComparison.OrdinalIgnoreCase)
            ? WorkMode.FastConnection : WorkMode.LowResource;
        set => WorkModeValue = value == WorkMode.FastConnection ? "fastConnection" : "lowResource";
    }
}

public sealed class StartupSettings
{
    [JsonPropertyName("enable48V")] public bool Enable48V { get; set; } = false;
    [JsonPropertyName("enableEFX")] public bool EnableEFX { get; set; } = false;
    [JsonPropertyName("reapplyOnReconnect")] public bool ReapplyOnReconnect { get; set; } = true;
    [JsonPropertyName("phantomPowerConsent")] public bool PhantomPowerConsent { get; set; } = false;
}

public sealed class PanelSettings
{
    [JsonPropertyName("alwaysOnTop")] public bool AlwaysOnTop { get; set; } = false;
}

public sealed class LoggingSettings
{
    [JsonPropertyName("maxFileSizeMB")] public int MaxFileSizeMB { get; set; } = 10;
    [JsonPropertyName("maxFiles")] public int MaxFiles { get; set; } = 5;
}

/// <summary>NativeAOT 兼容的 Source Generator JSON 上下文。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(StartupSettings))]
[JsonSerializable(typeof(PanelSettings))]
[JsonSerializable(typeof(LoggingSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
