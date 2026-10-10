using System.Windows;
using System.Windows.Controls;
using SonicCube.Panel.Localization;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Views;

/// <summary>
/// 回放通道(USB / OTG)。内录(loopback) + 音量 + MUTE + 电平。
/// 推子 location:USB=2、OTG=3;loopback location 0=USB、1=OTG。
/// </summary>
public partial class PlaybackChannelView : UserControl
{
    private readonly int _index; // 0=USB, 1=OTG
    private bool _suppress;

    public event Action<uint, int>? SetIntRequested;
    public event Action<uint, float>? SetFloatRequested;

    public PlaybackChannelView(int index)
    {
        _index = index;
        InitializeComponent();
        WireEvents();
        ApplyLocalization();
        Loc.LanguageChanged += ApplyLocalization;
    }

    private int FaderLocation => _index == 0 ? 2 : 3;

    private void WireEvents()
    {
        BtnLoopback.Click += (_, _) => { if (_suppress) return; SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.Loopback, _index), BtnLoopback.IsChecked == true ? 1 : 0); };
        BtnMute.Click += (_, _) => { if (_suppress) return; SetIntRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.MuteSwitch, FaderLocation), BtnMute.IsChecked == true ? 1 : 0); };
        Fader.ValueCommitted += v => SetFloatRequested?.Invoke(CommandAddress.WithLocation(CommandAddress.VolumePot, FaderLocation), (float)v);
        Fader.ValueChanging += v => FaderValue.Text = ValueMapping.FaderText(v);
    }

    public void ApplyState(PresetState p)
    {
        _suppress = true;
        BtnLoopback.IsChecked = p.LoopbackSw[_index] == 1;
        BtnMute.IsChecked = p.MuteSw[FaderLocation] == 1;
        _suppress = false;
        Fader.SetDbSilently(p.VolPot[FaderLocation]);
        FaderValue.Text = ValueMapping.FaderText(p.VolPot[FaderLocation]);
    }

    public void SetMeter(float l, float r) => Meter.SetLevelDb(l, r);

    private void ApplyLocalization()
    {
        Title.Text = Loc.Get(_index == 0 ? "ChUsbPlayback" : "ChOtgPlayback");
        BtnLoopback.Content = Loc.Get("Loopback");
        BtnMute.Content = Loc.Get("Mute");
    }
}
