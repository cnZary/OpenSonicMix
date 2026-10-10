using SonicCube.Panel.Localization;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Forms;

/// <summary>
/// 设置窗体(见需求 6.4/11)。
/// 工作模式切换立即通知 Tray;低占用模式下重连复选框置灰但保留配置值;48V 安全确认持久化。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly SonicCube.Panel.Ipc.TrayClient _client;
    private SettingsDto _settings;

    private ComboBox _lang = null!;
    private CheckBox _startWithWindows = null!;
    private RadioButton _modeLow = null!, _modeFast = null!;
    private CheckBox _enable48V = null!, _enableEFX = null!;
    private CheckBox _reapply = null!;
    private Label _reapplyHint = null!;
    private CheckBox _alwaysOnTop = null!;
    private Button _openLog = null!, _close = null!;
    private GroupBox _gGeneral = null!, _gConn = null!, _gStartup = null!, _gReconnect = null!, _gOther = null!;
    private bool _loading;

    /// <summary>设置已修改并保存。</summary>
    public event Action<SettingsDto>? SettingsSaved;

    public SettingsForm(SonicCube.Panel.Ipc.TrayClient client, SettingsDto current)
    {
        _client = client;
        _settings = current;
        BuildUi();
        LoadFrom(current);
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private void BuildUi()
    {
        Text = "Settings";
        Width = 460; Height = 560;
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.FromArgb(220, 220, 225);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        int y = 12;
        _gGeneral = Group("General", 12, y, 420, 76);
        _lang = new ComboBox { Left = 150, Top = 22, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _lang.Items.AddRange(new object[] { "Auto", "English", "简体中文" });
        var langLabel = LabelFor("Language", 14, 26);
        _startWithWindows = Check("StartWithWindows", 14, 48);
        _gGeneral.Controls.Add(langLabel); _gGeneral.Controls.Add(_lang); _gGeneral.Controls.Add(_startWithWindows);
        y += 84;

        _gConn = Group("Connection", 12, y, 420, 96);
        var modeLabel = LabelFor("MidiWorkMode", 14, 22);
        _modeLow = Radio("ModeLowResource", 20, 44);
        _modeFast = Radio("ModeFastConnection", 20, 68);
        _gConn.Controls.Add(modeLabel); _gConn.Controls.Add(_modeLow); _gConn.Controls.Add(_modeFast);
        y += 104;

        _gStartup = Group("StartupBehavior", 12, y, 420, 76);
        _enable48V = Check("AutoEnable48V", 14, 24);
        _enableEFX = Check("AutoEnableEFX", 14, 48);
        _gStartup.Controls.Add(_enable48V); _gStartup.Controls.Add(_enableEFX);
        y += 84;

        _gReconnect = Group("DeviceReconnect", 12, y, 420, 76);
        _reapply = Check("ReapplyOnReconnect", 14, 24);
        _reapplyHint = new Label { Left = 30, Top = 48, AutoSize = true, ForeColor = Color.FromArgb(150, 150, 155), Font = new Font("Segoe UI", 8) };
        _gReconnect.Controls.Add(_reapply); _gReconnect.Controls.Add(_reapplyHint);
        y += 84;

        _gOther = Group("Other", 12, y, 420, 76);
        _alwaysOnTop = Check("AlwaysOnTop", 14, 24);
        var logLabel = LabelFor("LogDir", 14, 50);
        _openLog = new Button { Left = 150, Top = 46, Width = 80, Text = Loc.Get("Open") };
        _gOther.Controls.Add(_alwaysOnTop); _gOther.Controls.Add(logLabel); _gOther.Controls.Add(_openLog);
        y += 84;

        _close = new Button { Left = 352, Top = y + 4, Width = 80, Text = "OK", DialogResult = DialogResult.OK };

        Controls.AddRange(new Control[] { _gGeneral, _gConn, _gStartup, _gReconnect, _gOther, _close });

        // 事件
        _lang.SelectedIndexChanged += async (_, _) => await OnLanguageChangedAsync();
        _startWithWindows.CheckedChanged += async (_, _) => await SaveAsync();
        _modeLow.CheckedChanged += async (_, _) => await OnModeChangedAsync();
        _modeFast.CheckedChanged += async (_, _) => await OnModeChangedAsync();
        _enable48V.CheckedChanged += async (_, _) => await OnEnable48VChangedAsync();
        _enableEFX.CheckedChanged += async (_, _) => await SaveAsync();
        _reapply.CheckedChanged += async (_, _) => await SaveAsync();
        _alwaysOnTop.CheckedChanged += async (_, _) => await SaveAsync();
        _openLog.Click += (_, _) => OpenLogDir();
    }

    private GroupBox Group(string key, int x, int y, int w, int h) =>
        new() { Left = x, Top = y, Width = w, Height = h, ForeColor = ForeColor, Text = Loc.Get(key), Name = "grp_" + key };

    private Label LabelFor(string key, int x, int y) =>
        new() { Left = x, Top = y, AutoSize = true, Text = Loc.Get(key), Name = "lbl_" + key };

    private CheckBox Check(string key, int x, int y) =>
        new() { Left = x, Top = y, AutoSize = true, Text = Loc.Get(key), Name = "chk_" + key, ForeColor = ForeColor };

    private RadioButton Radio(string key, int x, int y) =>
        new() { Left = x, Top = y, AutoSize = true, Text = Loc.Get(key), Name = "rdo_" + key, ForeColor = ForeColor };

    private void LoadFrom(SettingsDto s)
    {
        _loading = true;
        _lang.SelectedIndex = s.Language switch
        {
            "en-US" => 1,
            "zh-CN" => 2,
            _ => 0,
        };
        _startWithWindows.Checked = s.StartWithWindows ?? false;
        bool fast = string.Equals(s.WorkMode, "fastConnection", StringComparison.OrdinalIgnoreCase);
        _modeFast.Checked = fast;
        _modeLow.Checked = !fast;
        _enable48V.Checked = s.Enable48V ?? true;
        _enableEFX.Checked = s.EnableEFX ?? true;
        _reapply.Checked = s.ReapplyOnReconnect ?? true;
        _alwaysOnTop.Checked = s.AlwaysOnTop ?? false;
        UpdateReapplyEnabled(fast);
        _loading = false;
    }

    private void UpdateReapplyEnabled(bool fast)
    {
        // 低占用模式:置灰不可改,但保留勾选值(不清空 _reapply.Checked)。
        _reapply.Enabled = fast;
        _reapplyHint.Visible = !fast;
    }

    private bool IsFast => _modeFast.Checked;

    private async Task OnModeChangedAsync()
    {
        if (_loading || !_modeLow.Checked && !_modeFast.Checked) return;
        UpdateReapplyEnabled(IsFast);
        // 工作模式切换立即通知 Tray 生效。
        await _client.SetWorkModeAsync(IsFast ? "fastConnection" : "lowResource");
        await SaveAsync();
    }

    private bool _phantomConsentGrantedThisSession;

    private async Task OnEnable48VChangedAsync()
    {
        if (_loading) return;
        if (_enable48V.Checked && !(_settings.PhantomPowerConsent ?? false) && !_phantomConsentGrantedThisSession)
        {
            // 首次启用 48V 自动开启需安全确认。
            if (!ShowPhantomConsent())
            {
                _loading = true;
                _enable48V.Checked = false;
                _loading = false;
                return;
            }
            _phantomConsentGrantedThisSession = true;
        }
        await SaveAsync();
    }

    /// <summary>48V 安全确认对话框(中英文)。</summary>
    private bool ShowPhantomConsent()
    {
        using var dlg = new Form
        {
            Text = Loc.Get("PhantomWarningTitle"),
            Width = 440, Height = 220,
            BackColor = Color.FromArgb(32, 32, 36),
            ForeColor = Color.FromArgb(220, 220, 225),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
        };
        var msg = new Label { Left = 16, Top = 16, Width = 396, Height = 80, Text = Loc.Get("PhantomWarning") };
        var consent = new CheckBox { Left = 16, Top = 100, Width = 396, Text = Loc.Get("PhantomConsent") };
        var ok = new Button { Left = 220, Top = 140, Width = 90, Text = "OK", DialogResult = DialogResult.OK, Enabled = false };
        var cancel = new Button { Left = 318, Top = 140, Width = 90, Text = Loc.Get("Reset") == "重置" ? "取消" : "Cancel", DialogResult = DialogResult.Cancel };
        consent.CheckedChanged += (_, _) => ok.Enabled = consent.Checked;
        dlg.Controls.AddRange(new Control[] { msg, consent, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK && consent.Checked;
    }

    private async Task OnLanguageChangedAsync()
    {
        if (_loading) return;
        string lang = _lang.SelectedIndex switch { 1 => "en-US", 2 => "zh-CN", _ => "auto" };
        Loc.Apply(lang);
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_loading) return;
        var dto = new SettingsDto
        {
            Language = _lang.SelectedIndex switch { 1 => "en-US", 2 => "zh-CN", _ => "auto" },
            StartWithWindows = _startWithWindows.Checked,
            WorkMode = IsFast ? "fastConnection" : "lowResource",
            Enable48V = _enable48V.Checked,
            EnableEFX = _enableEFX.Checked,
            ReapplyOnReconnect = _reapply.Checked,
            AlwaysOnTop = _alwaysOnTop.Checked,
            // 48V 授权:仅当本次明确通过确认才置 true;取消勾选 48V 时清除授权(更安全);否则保留原值。
            PhantomPowerConsent = !_enable48V.Checked
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
        Text = Loc.Get("Settings");
        _gGeneral.Text = Loc.Get("General");
        _gConn.Text = Loc.Get("Connection");
        _gStartup.Text = Loc.Get("StartupBehavior");
        _gReconnect.Text = Loc.Get("DeviceReconnect");
        _gOther.Text = Loc.Get("Other");
        _startWithWindows.Text = Loc.Get("StartWithWindows");
        _modeLow.Text = Loc.Get("ModeLowResource");
        _modeFast.Text = Loc.Get("ModeFastConnection");
        _enable48V.Text = Loc.Get("AutoEnable48V");
        _enableEFX.Text = Loc.Get("AutoEnableEFX");
        _reapply.Text = Loc.Get("ReapplyOnReconnect");
        _reapplyHint.Text = Loc.Get("OnlyFastMode");
        _alwaysOnTop.Text = Loc.Get("AlwaysOnTop");
        _openLog.Text = Loc.Get("Open");
        foreach (Control c in _gGeneral.Controls) if (c.Name == "lbl_Language") c.Text = Loc.Get("Language");
        foreach (Control c in _gConn.Controls) if (c.Name == "lbl_MidiWorkMode") c.Text = Loc.Get("MidiWorkMode");
        foreach (Control c in _gOther.Controls) if (c.Name == "lbl_LogDir") c.Text = Loc.Get("LogDir");
    }
}
