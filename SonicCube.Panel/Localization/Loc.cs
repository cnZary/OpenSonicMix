using System.Globalization;
using System.Resources;

namespace SonicCube.Panel.Localization;

/// <summary>
/// Panel 本地化服务(见需求第十三节,resx + 卫星程序集)。
/// 默认跟随系统;用户可手动选择;切换无需重启(动态刷新所有已注册控件)。
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Rm = new("SonicCube.Panel.Localization.Strings", typeof(Loc).Assembly);
    internal static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    /// <summary>当前 UI 文化。</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>语言变更事件(动态刷新)。</summary>
    public static event Action? LanguageChanged;

    /// <summary>应用配置语言("auto"/"zh-CN"/"en-US")。</summary>
    public static void Apply(string configured)
    {
        CultureInfo target;
        if (string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(configured))
            target = SystemUiCulture;
        else
        {
            try { target = CultureInfo.GetCultureInfo(configured); }
            catch { target = SystemUiCulture; }
        }
        SetCulture(target);
    }

    public static void SetCulture(CultureInfo culture)
    {
        if (Equals(Culture.Name, culture.Name)) return;
        Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key)
        => Rm.GetString(key, Culture) ?? key;

    /// <summary>按当前文化格式化带占位符的资源串({0}…)。</summary>
    public static string Format(string key, params object[] args)
        => string.Format(Culture, Get(key), args);
}
