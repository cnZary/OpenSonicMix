using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SonicCube.Panel.Controls;

namespace SonicCube.Panel.Tests;

[STATestClass]
public sealed class ControlInteractionTests
{
    [TestMethod]
    public void Volume_ExternalChangeAllowsPreviouslySentValueAndFailedWriteCanRetry()
    {
        var fader = new VolumeFaderControl();
        var sent = new List<double>();
        fader.ValueCommitted += sent.Add;
        fader.SetDbSilently(-12);
        Submit(fader, -6);
        fader.SetDbSilently(-6);
        fader.SetDbSilently(-12);
        Submit(fader, -6);
        CollectionAssert.AreEqual(new double[] { -6, -6 }, sent);
        fader.CompletePendingWrite(-6, false);
        fader.SetDbSilently(-12);
        Assert.AreEqual(-12, fader.Db);
        Submit(fader, -6);
        Assert.HasCount(3, sent);
    }

    [TestMethod]
    public void Volume_StaleCompletionDoesNotReleaseNewerTargetAndResetAcceptsAuthority()
    {
        var fader = new VolumeFaderControl();
        fader.SetDbSilently(-12);
        Submit(fader, -6);
        Submit(fader, -3);
        fader.CompletePendingWrite(-6, false);
        fader.SetDbSilently(-12);
        Assert.AreEqual(-3, fader.Db);
        fader.ResetSynchronization();
        fader.SetDbSilently(-18);
        Assert.AreEqual(-18, fader.Db);
        fader.CompletePendingWrite(-3, true);
        Assert.AreEqual(-18, fader.Db);
    }

    [TestMethod]
    public void Volume_QuantizedSuccessfulReadbackDoesNotRemainBlocked()
    {
        var fader = new VolumeFaderControl();
        fader.SetDbSilently(-12);
        Submit(fader, -3.2);
        fader.SetDbSilently(-3);
        fader.CompletePendingWrite(-3.2, true);
        fader.SetDbSilently(-3);
        Assert.AreEqual(-3, fader.Db);
    }

    [TestMethod]
    public void Volume_CompletionWhileNewerValueIsQueuedDoesNotReplacePreview()
    {
        var fader = new VolumeFaderControl();
        fader.SetDbSilently(-12);
        Submit(fader, -6);
        fader.Db = -3;
        Invoke(fader, "QueueSend", -3.0);
        fader.CompletePendingWrite(-6, false);
        fader.SetDbSilently(-12);
        Assert.AreEqual(-3, fader.Db);
        Invoke(fader, "FlushPending");
        Assert.AreEqual(-3, Field<double>(fader, "_awaitingConfirmDb"));
    }

    [TestMethod]
    public void Volume_InterruptedDragKeepsAlreadySentValueAndNormalReleaseCommitsOnce()
    {
        var fader = new VolumeFaderControl();
        var sent = new List<double>();
        fader.ValueCommitted += sent.Add;
        fader.SetDbSilently(-12);
        Submit(fader, -6);
        SetField(fader, "_dragging", true);
        fader.Db = -3;
        Invoke(fader, "QueueSend", -3.0);
        Interrupt(fader, "capture");
        Assert.AreEqual(-6, fader.Db);
        Assert.HasCount(1, sent);
        fader.CompletePendingWrite(-6, true);
        SetField(fader, "_dragging", true);
        fader.Db = -3;
        Invoke(fader, "QueueSend", -3.0);
        Invoke(fader, "OnMouseLeftButtonUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Interrupt(fader, "capture");
        Invoke(fader, "FlushPending");
        CollectionAssert.AreEqual(new double[] { -6, -3 }, sent);
    }

    [TestMethod]
    public void Volume_AllValuePathsNotifyTheSameReadoutAndStaleSnapshotDoesNot()
    {
        var fader = new VolumeFaderControl();
        double readout = double.NaN;
        fader.ValueChanging += value => readout = value;
        fader.SetDbSilently(-12);
        Submit(fader, -6);
        Assert.AreEqual(-6, readout);
        fader.SetDbSilently(-12);
        Assert.AreEqual(fader.Db, readout);
        Invoke(fader, "OnMouseWheel", new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent });
        Assert.AreEqual(-5, readout);
        fader.CompletePendingWrite(-5, false);
        Assert.AreEqual(-12, readout);
    }

    [TestMethod]
    [DataRow(Key.Up, -11.0)]
    [DataRow(Key.Down, -13.0)]
    [DataRow(Key.PageUp, -7.0)]
    [DataRow(Key.PageDown, -17.0)]
    [DataRow(Key.Home, 10.0)]
    [DataRow(Key.End, -51.0)]
    public void Volume_KeyboardUpdatesThumbReadoutAndFinalWriteTogether(Key key, double expected)
    {
        var fader = new VolumeFaderControl();
        fader.SetDbSilently(-12);
        double readout = -12;
        var sent = new List<double>();
        fader.ValueChanging += value => readout = value;
        fader.ValueCommitted += sent.Add;
        fader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        Assert.AreEqual(expected, fader.Db);
        Assert.AreEqual(expected, readout);
        CollectionAssert.AreEqual(new[] { expected }, sent);
        fader.SetDbSilently(-12);
        Assert.AreEqual(expected, fader.Db, "An earlier snapshot must not replace the keyboard preview.");
        Assert.AreEqual(expected, readout);
        fader.CompletePendingWrite(expected, false);
        Assert.AreEqual(-12, fader.Db);
        Assert.AreEqual(-12, readout);
    }

    [TestMethod]
    [DataRow("capture")]
    [DataRow("disabled")]
    [DataRow("unloaded")]
    public void Volume_InterruptedDragDropsUnsentBatchAndStopsItsTimer(string interruption)
    {
        var fader = new VolumeFaderControl();
        int sent = 0;
        fader.ValueCommitted += _ => sent++;
        fader.SetDbSilently(-12);
        SetField(fader, "_dragging", true);
        fader.Db = -3;
        Invoke(fader, "QueueSend", -3.0);
        Interrupt(fader, interruption);
        Invoke(fader, "FlushPending");
        Assert.AreEqual(0, sent);
        Assert.AreEqual(-12, fader.Db);
        Assert.IsFalse(Field<DispatcherTimer>(fader, "_sendTimer").IsEnabled);
        fader.SetDbSilently(-18);
        Assert.AreEqual(-18, fader.Db);
    }

    [TestMethod]
    [DataRow("capture")]
    [DataRow("disabled")]
    [DataRow("unloaded")]
    public void OtherControls_InterruptedDragRestoresLatestSnapshotWithoutCommit(string interruption)
    {
        var knob = new KnobControl();
        knob.Configure(0, 100, 1, 0, "Send");
        var linear = new LinearFaderControl();
        var pan = new PanSliderControl();
        int sent = 0;
        knob.ValueCommitted += _ => sent++;
        linear.ValueCommitted += _ => sent++;
        pan.ValueCommitted += _ => sent++;
        foreach (var control in new FrameworkElement[] { knob, linear, pan })
        {
            Invoke(control, "SetValueSilently", 10.0);
            SetField(control, "_dragging", true);
            control.SetValue(control is KnobControl ? KnobControl.ValueProperty : control is LinearFaderControl ? LinearFaderControl.ValueProperty : PanSliderControl.ValueProperty, 30.0);
            Invoke(control, "SetValueSilently", 20.0);
            Interrupt(control, interruption);
            Assert.IsFalse(Field<bool>(control, "_dragging"));
            Assert.AreEqual(20.0, (double)control.GetType().GetProperty("Value")!.GetValue(control)!);
        }
        Assert.AreEqual(0, sent);
    }

    [TestMethod]
    public void Knob_DisableCancelsUncommittedText()
    {
        var knob = new KnobControl();
        knob.Configure(0, 100, 1, 10, "Send");
        int sent = 0;
        knob.ValueCommitted += _ => sent++;
        var editor = Field<TextBox>(knob, "_editor");
        editor.Text = "90";
        editor.Visibility = Visibility.Visible;
        knob.IsEnabled = false;
        Invoke(knob, "CommitEditor");
        Assert.AreEqual(10, knob.Value);
        Assert.AreEqual(0, sent);
        Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
    }

    private static void Submit(VolumeFaderControl fader, double value)
    {
        fader.Db = value;
        Invoke(fader, "QueueSend", value);
        Invoke(fader, "FlushPending");
    }

    [TestMethod]
    [DataRow("knob")]
    [DataRow("pan")]
    [DataRow("linear")]
    public void FailedContinuousWrite_RestoresTheOnlySnapshotAndAllowsRetry(string kind)
    {
        var control = CreateContinuousControl(kind);
        Invoke(control, "SetValueSilently", 10.0);
        Wheel(control);
        Assert.AreEqual(11.0, ContinuousValue(control));
        Invoke(control, "SetValueSilently", 12.0);
        Assert.AreEqual(11.0, ContinuousValue(control), "The readback is retained while echo is suppressed.");
        Invoke(control, "FailPendingWrite", 11.0);
        Assert.AreEqual(12.0, ContinuousValue(control), "Failure must show the retained readback immediately.");
        Wheel(control);
        Assert.AreEqual(13.0, ContinuousValue(control));
        Invoke(control, "SetValueSilently", 12.0);
        Invoke(control, "FailPendingWrite", 13.0);
        Assert.AreEqual(12.0, ContinuousValue(control));
    }

    [TestMethod]
    [DataRow("knob")]
    [DataRow("pan")]
    [DataRow("linear")]
    public void OldContinuousFailure_DoesNotReplaceNewerValueOrNewDrag(string kind)
    {
        var control = CreateContinuousControl(kind);
        Invoke(control, "SetValueSilently", 10.0);
        Wheel(control);
        Wheel(control);
        Invoke(control, "SetValueSilently", 10.0);
        Invoke(control, "FailPendingWrite", 11.0);
        Assert.AreEqual(12.0, ContinuousValue(control));
        // A new drag may initially still have the previous committed value.
        SetField(control, "_dragging", true);
        Invoke(control, "FailPendingWrite", 12.0);
        Assert.AreEqual(12.0, ContinuousValue(control));
        Interrupt(control, "capture");
        Assert.AreEqual(10.0, ContinuousValue(control));
    }

    [TestMethod]
    [DataRow("knob", "disabled")]
    [DataRow("knob", "unloaded")]
    [DataRow("pan", "disabled")]
    [DataRow("pan", "unloaded")]
    [DataRow("linear", "disabled")]
    [DataRow("linear", "unloaded")]
    public void ContinuousWrite_InterruptedAfterWheelRestoresAuthorityWithoutDrag(string kind, string interruption)
    {
        var control = CreateContinuousControl(kind);
        Invoke(control, "SetValueSilently", 10.0);
        Wheel(control);
        Invoke(control, "SetValueSilently", 12.0);
        Interrupt(control, interruption);
        Assert.AreEqual(12.0, ContinuousValue(control));
        Invoke(control, "SetValueSilently", 14.0);
        Assert.AreEqual(14.0, ContinuousValue(control), "Interruption must clear the echo window too.");
    }

    [TestMethod]
    public void Knob_FastTextFailureRestoresAuthorityButEarlierFailurePreservesAnActiveEditor()
    {
        var knob = new KnobControl();
        knob.Configure(0, 100, 1, 10, "Send");
        var editor = Field<TextBox>(knob, "_editor");
        knob.ValueCommitted += value =>
        {
            knob.SetValueSilently(12);
            knob.FailPendingWrite(value);
        };
        editor.Text = "30";
        editor.Visibility = Visibility.Visible;
        Invoke(knob, "CommitEditor");
        Assert.AreEqual(12, knob.Value);
        knob.Value = 30;
        editor.Text = "40";
        editor.Visibility = Visibility.Visible;
        knob.FailPendingWrite(30);
        Assert.AreEqual(30, knob.Value);
        Assert.AreEqual("40", editor.Text);
    }

    private static FrameworkElement CreateContinuousControl(string kind) => kind switch
    {
        "knob" => new KnobControl(),
        "pan" => new PanSliderControl(),
        _ => new LinearFaderControl(),
    };

    private static double ContinuousValue(FrameworkElement control) => (double)control.GetType().GetProperty("Value")!.GetValue(control)!;

    private static void Wheel(FrameworkElement control) => Invoke(control, "OnMouseWheel",
        new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent });

    internal static void Interrupt(FrameworkElement control, string interruption)
    {
        if (interruption == "disabled") control.IsEnabled = false;
        else if (interruption == "unloaded") control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        else control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
    }

    internal static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(owner, args);
    internal static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    internal static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
