using System.Windows;
using System.Windows.Threading;
using SonicCube.Panel.Startup;

namespace SonicCube.Panel.Tests;

[STATestClass]
public sealed class PanelInstanceTests
{
    [TestMethod]
    public void DeviceAndSimulation_CanOwnTheirInstancesIndependently()
    {
        string root = InstanceRoot();
        using var device = new PanelInstance(false, root);
        using var simulation = new PanelInstance(true, root);
        Assert.IsTrue(device.IsOwner);
        Assert.IsTrue(simulation.IsOwner);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SecondInstanceInTheSameMode_IsNotOwner(bool simulate)
    {
        string root = InstanceRoot();
        using var owner = new PanelInstance(simulate, root);
        Assert.IsTrue(owner.IsOwner);
        using (var secondary = new PanelInstance(simulate, root))
            Assert.IsFalse(secondary.IsOwner);
        using var anotherSecondary = new PanelInstance(simulate, root);
        Assert.IsFalse(anotherSecondary.IsOwner, "Disposing a secondary must not release the owner.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DisposedOwner_CanBeAcquiredAgain(bool simulate)
    {
        string root = InstanceRoot();
        using (var owner = new PanelInstance(simulate, root))
            Assert.IsTrue(owner.IsOwner);
        using var replacement = new PanelInstance(simulate, root);
        Assert.IsTrue(replacement.IsOwner);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Activation_RestoresOnlyTheRegisteredWindowForItsMode(bool registerBeforeShow)
    {
        string root = InstanceRoot();
        using var device = new PanelInstance(false, root);
        using var simulation = new PanelInstance(true, root);
        using var deviceSecondary = new PanelInstance(false, root);
        using var simulationSecondary = new PanelInstance(true, root);
        Window? deviceWindow = null, simulationWindow = null;
        try
        {
            deviceWindow = CreateWindow("Panel instance regression: device");
            simulationWindow = CreateWindow("Panel instance regression: simulation");
            Register(device, deviceWindow, registerBeforeShow);
            Register(simulation, simulationWindow, registerBeforeShow);
            deviceWindow.WindowState = WindowState.Minimized;
            simulationWindow.WindowState = WindowState.Minimized;
            DrainDispatcher();

            Assert.IsTrue(CompleteWithDispatcher(deviceSecondary.ActivateExistingAsync()));
            DrainDispatcher();
            Assert.AreEqual(WindowState.Normal, deviceWindow.WindowState);
            Assert.AreEqual(WindowState.Minimized, simulationWindow.WindowState,
                "Activating device mode must leave simulation mode alone.");

            Assert.IsTrue(CompleteWithDispatcher(simulationSecondary.ActivateExistingAsync()));
            DrainDispatcher();
            Assert.AreEqual(WindowState.Normal, simulationWindow.WindowState);
        }
        finally
        {
            try { simulationWindow?.Close(); }
            finally { deviceWindow?.Close(); }
        }
    }

    private static string InstanceRoot() =>
        $@"Local\SonicCube.Panel.Tests.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private static Window CreateWindow(string title) => new()
    {
        Title = title,
        Width = 180,
        Height = 100,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
    };

    private static void Register(PanelInstance instance, Window window, bool beforeShow)
    {
        if (beforeShow) instance.RegisterWindow(window);
        window.Show();
        if (!beforeShow) instance.RegisterWindow(window);
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static bool CompleteWithDispatcher(Task<bool> activation)
    {
        if (activation.IsCompleted) return activation.GetAwaiter().GetResult();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        timeout.Tick += (_, _) => frame.Continue = false;
        _ = activation.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
        try
        {
            timeout.Start();
            Dispatcher.PushFrame(frame);
            Assert.IsTrue(activation.IsCompleted, "Registered window activation timed out.");
            return activation.GetAwaiter().GetResult();
        }
        finally { timeout.Stop(); }
    }
}
