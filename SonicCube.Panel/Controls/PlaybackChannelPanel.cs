using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;

namespace SonicCube.Panel.Controls;

using Panel = System.Windows.Forms.Panel;

/// <summary>
/// 回放通道面板(USB / OTG)。location:USB 推子=2、OTG 推子=3;loopback location 0=USB,1=OTG。
/// LOOPBACK 按钮 + 音量 + MUTE + 电平。
/// </summary>
public sealed class PlaybackChannelPanel : UserControl
{
    private readonly int _index; // 0=USB, 1=OTG
    private ToggleButton _btnLoopback = null!, _btnMute = null!;
    private VolumeFader _fader = null!;
    private StereoMeter _meter = null!;
    private Label _title = null!;

    public event Action<uint, int>? SetIntRequested;
    public event Action<uint, float>? SetFloatRequested;

    public PlaybackChannelPanel(int index)
    {
        _index = index;
        BackColor = Color.FromArgb(36, 36, 40);
        ForeColor = Color.FromArgb(220, 220, 225);
        BuildUi();
        WireEvents();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private int FaderLocation => _index == 0 ? 2 : 3;
    private int MuteLocation => _index == 0 ? 2 : 3;

    private void BuildUi()
    {
        Width = 150; Height = 340;
        _title = new Label { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        var topRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 3, 0, 0), WrapContents = false };
        _btnLoopback = new ToggleButton { Width = 78 };
        topRow.Controls.Add(_btnLoopback);

        var bottom = new Panel { Dock = DockStyle.Fill };
        _fader = new VolumeFader { Left = 18, Top = 4, Width = 50, Height = 210 };
        _meter = new StereoMeter { Left = 96, Top = 4, Width = 36, Height = 210 };
        var msRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 3, 0, 0), WrapContents = false };
        _btnMute = new ToggleButton { Width = 58 };
        msRow.Controls.Add(_btnMute);
        bottom.Controls.Add(_fader);
        bottom.Controls.Add(_meter);
        bottom.Controls.Add(msRow);

        Controls.Add(bottom);
        Controls.Add(topRow);
        Controls.Add(_title);
    }

    private void WireEvents()
    {
        _btnLoopback.CheckedChanged += v =>
            SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Loopback, _index), v ? 1 : 0);
        _btnMute.CheckedChanged += v =>
            SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.MuteSwitch, MuteLocation), v ? 1 : 0);
        _fader.ValueCommitted += v =>
            SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.VolumePot, FaderLocation), v);
    }

    public void ApplyState(Protocol.Models.PresetState p, float[]? meters)
    {
        _btnLoopback.SetCheckedSilently(p.LoopbackSw[_index] == 1);
        _btnMute.SetCheckedSilently(p.MuteSw[MuteLocation] == 1);
        _fader.SetDbSilently(p.VolPot[FaderLocation]);
        if (meters != null)
        {
            int mi = _index == 0 ? 4 : 6;
            _meter.SetLevel(meters[mi], meters[mi + 1]);
        }
    }

    public void SetMeter(float l, float r) => _meter.SetLevel(l, r);

    private void ApplyLocalization()
    {
        _title.Text = Loc.Get(_index == 0 ? "ChUsbPlayback" : "ChOtgPlayback");
        _btnLoopback.Text = Loc.Get("Loopback");
        _btnMute.Text = Loc.Get("Mute");
    }
}
