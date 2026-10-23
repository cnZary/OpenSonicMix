using SonicCube.Tray.Settings;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SonicCube-settings-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void SuccessfulUpdate_PersistsBeforePublishingAndDoesNotMutatePreviousSnapshot()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        var previous = store.Current;
        int notifications = 0;
        store.Changed += updated =>
        {
            notifications++;
            Assert.AreSame(updated, store.Current);
            var saved = new SettingsStore(path).Load();
            Assert.AreEqual("zh-CN", saved.Language);
            Assert.IsTrue(saved.Startup.EnableEFX);
            Assert.AreEqual(WorkMode.LowResource, saved.Mode);
        };
        Assert.IsTrue(store.TryApplyUpdate(s => { s.Language = "zh-CN"; s.Startup.EnableEFX = true; s.Mode = WorkMode.LowResource; }, out var error), error);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual("auto", previous.Language);
        Assert.IsFalse(previous.Startup.EnableEFX);
        Assert.AreEqual(WorkMode.FastConnection, previous.Mode);
        Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [TestMethod]
    public void WriteFailure_PreservesSettingsAndDoesNotNotifySuccess()
    {
        var blockedDirectory = Path.Combine(_directory, "blocked");
        File.WriteAllText(blockedDirectory, "This file prevents creating the settings directory.");
        var store = new SettingsStore(Path.Combine(blockedDirectory, "settings.json"));
        var previous = store.Current;
        int notifications = 0;
        store.Changed += _ => notifications++;
        Assert.IsFalse(store.TryApplyUpdate(s => { s.Startup.Enable48V = true; s.Mode = WorkMode.LowResource; }, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        Assert.AreSame(previous, store.Current);
        Assert.IsFalse(store.Current.Startup.Enable48V);
        Assert.AreEqual(WorkMode.FastConnection, store.Current.Mode);
        Assert.AreEqual(0, notifications);
        store.Save(); // Compatibility entry point must not send a success notification either.
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void MutationFailure_PreservesDiskAndDeeplyNestedPreviousValues()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        store.Save();
        string before = File.ReadAllText(path);
        Assert.IsFalse(store.TryApplyUpdate(s => { s.Panel.AlwaysOnTop = true; s.Startup.PhantomPowerConsent = true; throw new IOException("injected failure"); }, out var error));
        Assert.AreEqual("injected failure", error);
        Assert.IsFalse(store.Current.Panel.AlwaysOnTop);
        Assert.IsFalse(store.Current.Startup.PhantomPowerConsent);
        Assert.AreEqual(before, File.ReadAllText(path));
    }

    [TestMethod]
    public void FailedPersistence_CanBeRetriedAfterStorageRecovers()
    {
        var blockedDirectory = Path.Combine(_directory, "retry");
        File.WriteAllText(blockedDirectory, "block directory creation");
        var path = Path.Combine(blockedDirectory, "settings.json");
        var store = new SettingsStore(path);
        int notifications = 0;
        store.Changed += _ => notifications++;
        Assert.IsFalse(store.TryApplyUpdate(s => s.Language = "en", out _));
        Assert.AreEqual("auto", store.Current.Language);
        File.Delete(blockedDirectory);
        Assert.IsTrue(store.TryApplyUpdate(s => s.Language = "en", out var error), error);
        Assert.AreEqual("en", store.Current.Language);
        Assert.AreEqual("en", new SettingsStore(path).Load().Language);
        Assert.AreEqual(1, notifications);
    }
}
