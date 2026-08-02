using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Hotkeys;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;
using SmartMacro.Native.Hotkey;

namespace SmartMacro.Tests;

// W0.2b: hotkey bindings now come from the macro library instead of a hotkeys.json.
//
// The listener is exercised WITHOUT calling StartAsync, so the Win32 monitors never spawn
// their message-loop threads and never touch RegisterHotKey — they're inert until started.
// That keeps the binding-derivation logic (the part that actually changed) testable
// without faking sealed native plumbing or requiring a desktop session.
public class HotkeyListenerTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-hotkeys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MacroGraph WithTriggers(string name, params MacroTrigger[] triggers) => new()
    {
        Name = name,
        Triggers = [.. triggers],
        StartNodeId = "n0",
        Nodes = [new KeyPressNode { Id = "n0", Key = VirtualKey.F1, Target = new TargetSelector() }],
    };

    private static HotkeyListener CreateListener(MacroGraphStore store) => new(
        store,
        new Win32HotkeyMonitor(NullLogger<Win32HotkeyMonitor>.Instance),
        new Win32MouseHookMonitor(NullLogger<Win32MouseHookMonitor>.Instance),
        NullLogger<HotkeyListener>.Instance);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }

    [Test]
    public async Task Bindings_AreDerivedFromEveryHotkeyTriggerInTheLibrary()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance, seedDefaults: false);
            await store.SaveAsync(WithTriggers("immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
            await store.SaveAsync(WithTriggers("cursor",
                new HotkeyTrigger(HotkeyModifiers.Control, VirtualKey.F22),
                new HotkeyTrigger(HotkeyModifiers.None, 0, MouseButton.XButton1)));
            // Process triggers and trigger-less library macros contribute no chords.
            await store.SaveAsync(WithTriggers("boot", new ProcessAppearedTrigger("elementclient_64")));
            await store.SaveAsync(WithTriggers("helper"));

            using var listener = CreateListener(store);

            await Assert.That(listener.Bindings.Values.Order().ToList())
                .IsEquivalentTo(new List<string> { "cursor", "cursor", "immunity" });
            await Assert.That(listener.KeyboardBindings).Count().IsEqualTo(2);
            await Assert.That(listener.MouseBindings).Count().IsEqualTo(1);

            // Each registration carries its macro's chord, and its id resolves to the name.
            var immunity = listener.KeyboardBindings.Single(d => d.Key == VirtualKey.F23);
            await Assert.That(immunity.Modifiers).IsEqualTo(HotkeyModifiers.None);
            await Assert.That(listener.Bindings[immunity.Id]).IsEqualTo("immunity");

            var cursorKeyboard = listener.KeyboardBindings.Single(d => d.Key == VirtualKey.F22);
            await Assert.That(cursorKeyboard.Modifiers).IsEqualTo(HotkeyModifiers.Control);
            await Assert.That(listener.Bindings[cursorKeyboard.Id]).IsEqualTo("cursor");

            var cursorMouse = listener.MouseBindings[0];
            await Assert.That(cursorMouse.Button).IsEqualTo(MouseButton.XButton1);
            await Assert.That(listener.Bindings[cursorMouse.Id]).IsEqualTo("cursor");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task MacrosChanged_RebuildsTheBindingSet()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance, seedDefaults: false);
            await store.SaveAsync(WithTriggers("first", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));

            using var listener = CreateListener(store);
            await Assert.That(listener.Bindings.Values.Single()).IsEqualTo("first");

            // Rebinding a macro is an ordinary library edit — no separate config to sync.
            await store.SaveAsync(WithTriggers("first", new HotkeyTrigger(HotkeyModifiers.Shift, VirtualKey.F13)));
            await store.SaveAsync(WithTriggers("second", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F14)));

            var rebuilt = await WaitUntilAsync(() => listener.KeyboardBindings.Count == 2);
            await Assert.That(rebuilt).IsTrue();
            await Assert.That(listener.KeyboardBindings.Any(d => d.Key == VirtualKey.F23)).IsFalse();
            await Assert.That(listener.KeyboardBindings.Select(d => d.Key).Order().ToList())
                .IsEquivalentTo(new List<VirtualKey> { VirtualKey.F13, VirtualKey.F14 });

            // Deleting the macro takes its hotkey with it.
            await store.DeleteAsync("second");
            var shrunk = await WaitUntilAsync(() => listener.KeyboardBindings.Count == 1);
            await Assert.That(shrunk).IsTrue();
            await Assert.That(listener.Bindings.Values.Single()).IsEqualTo("first");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task MalformedTrigger_WithNeitherKeyNorMouseButton_IsSkipped()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance, seedDefaults: false);
            await store.SaveAsync(WithTriggers("broken", new HotkeyTrigger(HotkeyModifiers.Control, 0)));
            await store.SaveAsync(WithTriggers("fine", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F20)));

            using var listener = CreateListener(store);

            await Assert.That(listener.Bindings.Values.Single()).IsEqualTo("fine");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
