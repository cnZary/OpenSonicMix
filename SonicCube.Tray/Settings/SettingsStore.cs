using System.Text.Json;

namespace SonicCube.Tray.Settings;

/// <summary>
/// 配置加载/保存,目录 %AppData%\SonicCubePanel\settings.json。
/// 保留未知字段以外的语义;保存时序列化全部已知字段。
/// </summary>
public sealed class SettingsStore
{
    public static string ConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonicCubePanel");

    public static string SettingsPath { get; } = Path.Combine(ConfigDir, "settings.json");
    public static string LogsDir { get; } = Path.Combine(ConfigDir, "logs");

    private readonly object _sync = new();
    private readonly string _settingsPath;
    private AppSettings _current = new();
    public AppSettings Current => Volatile.Read(ref _current);

    public SettingsStore(string? settingsPath = null) => _settingsPath = settingsPath ?? SettingsPath;

    /// <summary>配置变更事件(保存后触发)。</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (s != null) Volatile.Write(ref _current, s);
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Settings", $"读取配置失败,使用默认值: {ex.Message}");
        }
        return Current;
    }

    public void Save() => TryApplyUpdate(_ => { }, out _);

    /// <summary>应用来自 IPC 的部分更新(仅更新提供的字段),保存并广播。</summary>
    public void ApplyUpdate(Action<AppSettings> mutate) => TryApplyUpdate(mutate, out _);

    /// <summary>先克隆与持久化，再发布新快照；失败不改变内存状态或发送成功通知。</summary>
    public bool TryApplyUpdate(Action<AppSettings> mutate, out string? error)
    {
        AppSettings next;
        lock (_sync)
        {
            string? temporaryPath = null;
            try
            {
                next = JsonSerializer.Deserialize(JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings),
                    SettingsJsonContext.Default.AppSettings)!;
                mutate(next);
                var directory = Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(next, SettingsJsonContext.Default.AppSettings));
                File.Move(temporaryPath, _settingsPath, overwrite: true);
                Volatile.Write(ref _current, next);
                error = null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logging.Logger.Error("Settings", $"保存配置失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (temporaryPath is not null)
                    try { File.Delete(temporaryPath); } catch { }
            }
        }
        Changed?.Invoke(next);
        return true;
    }
}
