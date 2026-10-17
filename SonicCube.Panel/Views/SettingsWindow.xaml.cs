using System.Windows;
using SonicCube.Panel.Ipc;
using SonicCube.Panel.Localization;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Views;

/// <summary>
/// 设置窗口。工作模式切换立即通知 Tray;低占用模式下重连复选框置灰但保留配置值;
/// 48V 自动开启需安全授权(持久化);语言切换无需重启。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly TrayClient _client;
    private SettingsDto _settings;
    private bool _loading;
    private bool _phantomConsentGrantedThisSession;

    public event Action<SettingsDto>? SettingsSaved;

    public SettingsWindow(TrayClient client, SettingsDto current)
    {
        _client = client;
        _settings = current;
        InitializeComponent();
        Lang.Items.Add(Loc.Get("LanguageAuto"));
        Lang.Items.Add("English");
        Lang.Items.Add("简体中文");
        LoadFrom(current);
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
        Closed += (_, _) => Loc.LanguageChanged -= ApplyLocalization;
        WireEvents();
    }

    private void WireEvents()
    {
        Lang.SelectionChanged += async (_, _) => await OnLanguageChangedAsync();
        // 自定义模板为非交互,补整框点击展开
        Lang.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Lang.IsDropDownOpen) return;
            Lang.IsDropDownOpen = true;
            e.Handled = true;
        };
        StartWithWindows.Checked += async (_, _) => await SaveAsync();
        StartWithWindows.Unchecked += async (_, _) => await SaveAsync();
        ModeLow.Checked += async (_, _) => await OnModeChangedAsync();
        ModeFast.Checked += async (_, _) => await OnModeChangedAsync();
        Enable48V.Checked += async (_, _) => await OnEnable48VChangedAsync();
        Enable48V.Unchecked += async (_, _) => await OnEnable48VChangedAsync();
        EnableEFX.Checked += async (_, _) => await SaveAsync();
        EnableEFX.Unchecked += async (_, _) => await SaveAsync();
        Reapply.Checked += async (_, _) => await SaveAsync();
        Reapply.Unchecked += async (_, _) => await SaveAsync();
        AlwaysOnTop.Checked += async (_, _) => await SaveAsync();
        AlwaysOnTop.Unchecked += async (_, _) => await SaveAsync();
        OpenLog.Click += (_, _) => OpenLogDir();
        CloseBtn.Click += (_, _) => Close();
    }

    private void LoadFrom(SettingsDto s)
    {
        _loading = true;
        Lang.SelectedIndex = s.Language switch { "en-US" => 1, "zh-CN" => 2, _ => 0 };
        StartWithWindows.IsChecked = s.StartWithWindows ?? false;
        bool fast = string.Equals(s.WorkMode, "fastConnection", StringComparison.OrdinalIgnoreCase);
        ModeFast.IsChecked = fast;
        ModeLow.IsChecked = !fast;
        Enable48V.IsChecked = s.Enable48V ?? true;
        EnableEFX.IsChecked = s.EnableEFX ?? true;
        Reapply.IsChecked = s.ReapplyOnReconnect ?? true;
        AlwaysOnTop.IsChecked = s.AlwaysOnTop ?? false;
        UpdateReapplyEnabled(fast);
        _loading = false;
    }

    private void UpdateReapplyEnabled(bool fast)
    {
        // 低占用模式:置灰不可改,但保留勾选值(不清空 Reapply.IsChecked)。
        Reapply.IsEnabled = fast;
        ReapplyHint.Visibility = fast ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool IsFast => ModeFast.IsChecked == true;

    private async Task OnModeChangedAsync()
    {
        if (_loading) return;
        UpdateReapplyEnabled(IsFast);
        await _client.SetWorkModeAsync(IsFast ? "fastConnection" : "lowResource");
        await SaveAsync();
    }

    private async Task OnEnable48VChangedAsync()
    {
        if (_loading) return;
        if (Enable48V.IsChecked == true && !(_settings.PhantomPowerConsent ?? false) && !_phantomConsentGrantedThisSession)
        {
            if (!ShowPhantomConsent())
            {
                _loading = true;
                Enable48V.IsChecked = false;
                _loading = false;
                return;
            }
            _phantomConsentGrantedThisSession = true;
        }
        await SaveAsync();
    }

    private bool ShowPhantomConsent()
    {
        var dlg = new Window
        {
            Title = Loc.Get("PhantomWarningTitle"),
            Width = 460, Height = 240,
            Owner = this,
            Background = (System.Windows.Media.Brush)FindResource("BrushWindow"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var msg = new System.Windows.Controls.TextBlock
        {
            Text = Loc.Get("PhantomWarning"),
            Foreground = (System.Windows.Media.Brush)FindResource("BrushText"),
            TextWrapping = TextWrapping.Wrap,
        };
        var consent = new System.Windows.Controls.CheckBox
        {
            Content = Loc.Get("PhantomConsent"),
            Foreground = (System.Windows.Media.Brush)FindResource("BrushText"),
            Style = (Style)FindResource("PlainCheck"),
            Margin = new Thickness(0, 12, 0, 14),
        };
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, Style = (Style)FindResource("PrimaryButton") };
        var cancel = new System.Windows.Controls.Button { Content = Loc.Get("Cancel"), Width = 90, Style = (Style)FindResource("SubtleButton") };
        consent.Checked += (_, _) => ok.IsEnabled = true;
        consent.Unchecked += (_, _) => ok.IsEnabled = false;
        ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        row.Children.Add(ok); row.Children.Add(cancel);
        panel.Children.Add(msg); panel.Children.Add(consent); panel.Children.Add(row);
        dlg.Content = panel;
        return dlg.ShowDialog() == true && consent.IsChecked == true;
    }

    private async Task OnLanguageChangedAsync()
    {
        if (_loading) return;
        string lang = Lang.SelectedIndex switch { 1 => "en-US", 2 => "zh-CN", _ => "auto" };
        Loc.Apply(lang);
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_loading) return;
        var dto = new SettingsDto
        {
            Language = Lang.SelectedIndex switch { 1 => "en-US", 2 => "zh-CN", _ => "auto" },
            StartWithWindows = StartWithWindows.IsChecked,
            WorkMode = IsFast ? "fastConnection" : "lowResource",
            Enable48V = Enable48V.IsChecked,
            EnableEFX = EnableEFX.IsChecked,
            ReapplyOnReconnect = Reapply.IsChecked,
            AlwaysOnTop = AlwaysOnTop.IsChecked,
            PhantomPowerConsent = Enable48V.IsChecked != true
                ? false
                : (_phantomConsentGrantedThisSession || (_settings.PhantomPowerConsent ?? false)),
        };
        var resp = await _client.UpdateSettingsAsync(dto);
        if (resp?.Success == true)
        {
            _settings = dto;
            SettingsSaved?.Invoke(dto);
        }
    }

    private void OpenLogDir()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonicCubePanel", "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void ApplyLocalization()
    {
        Title = Loc.Get("Settings");
        GGeneral.Header = Loc.Get("General");
        GConn.Header = Loc.Get("Connection");
        GStartup.Header = Loc.Get("StartupBehavior");
        GReconnect.Header = Loc.Get("DeviceReconnect");
        GOther.Header = Loc.Get("Other");
        LLanguage.Text = Loc.Get("Language");
        LMidiWorkMode.Text = Loc.Get("MidiWorkMode");
        LLogDir.Text = Loc.Get("LogDir");
        StartWithWindows.Content = Loc.Get("StartWithWindows");
        ModeLow.Content = Loc.Get("ModeLowResource");
        ModeFast.Content = Loc.Get("ModeFastConnection");
        Enable48V.Content = Loc.Get("AutoEnable48V");
        EnableEFX.Content = Loc.Get("AutoEnableEFX");
        Reapply.Content = Loc.Get("ReapplyOnReconnect");
        ReapplyHint.Text = Loc.Get("OnlyFastMode");
        AlwaysOnTop.Content = Loc.Get("AlwaysOnTop");
        OpenLog.Content = Loc.Get("Open");
        if (Lang.Items.Count > 0) Lang.Items[0] = Loc.Get("LanguageAuto");
    }
}
