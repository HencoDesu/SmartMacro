using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Hotkeys;
using SmartMacro.Macros.Bundle;
using SmartMacro.Macros.Model;
using SmartMacro.Macros.Storage;
using SmartMacro.Native;
using SmartMacro.Native.Hotkey;

namespace SmartMacro.Tests;

// W0.2b: привязки хоткеев приходят теперь из библиотеки макросов, а не из hotkeys.json.
//
// F3: библиотеку пишет ПАНЕЛЬ, поэтому тесты кладут бандлы в папку файлами и ждут наблюдателя —
// ровно тем путём, каким макрос попадает к демону в жизни. Отсюда же и главная новая проверка:
// у макроса, который не прошёл валидацию при загрузке, хоткей НЕ вооружается.
//
// Слушателя гоняют БЕЗ вызова StartAsync, поэтому мониторы Win32 не порождают своих потоков с
// циклом сообщений и не трогают RegisterHotKey — до запуска они безжизненны. Так логика вывода
// привязок (та самая часть, которая и поменялась) остаётся проверяемой без подделки запечатанной
// нативной обвязки и без сеанса рабочего стола.
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
        StartNodeId = Ids.Of("n0"),
        Nodes = [new KeyPressNode { Id = Ids.Of("n0"), DisplayName = "n0", Key = VirtualKey.F1, Target = new TargetSelector() }],
    };

    /// <summary>Кладёт бандл в <c>macros/</c> — тем же путём, каким это делает панель.</summary>
    private static void Write(string dir, MacroGraph graph) =>
        MacroBundleFolder.Save(MacroBundleFolder.In(dir), graph);

    private static void Delete(string dir, string name) =>
        MacroBundleFolder.Delete(MacroBundleFolder.In(dir), name);

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
            Write(dir, WithTriggers("immunity", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
            Write(dir, WithTriggers("cursor",
                new HotkeyTrigger(HotkeyModifiers.Control, VirtualKey.F22),
                new HotkeyTrigger(HotkeyModifiers.None, 0, MouseButton.XButton1)));
            // Триггеры по процессу и библиотечные макросы без триггеров аккордов не дают.
            Write(dir, WithTriggers("boot", new ProcessAppearedTrigger("elementclient_64")));
            Write(dir, WithTriggers("helper"));

            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            using var listener = CreateListener(store);

            await Assert.That(listener.Bindings.Values.Order().ToList())
                .IsEquivalentTo(new List<string> { "cursor", "cursor", "immunity" });
            await Assert.That(listener.KeyboardBindings).Count().IsEqualTo(2);
            await Assert.That(listener.MouseBindings).Count().IsEqualTo(1);

            // Каждая регистрация несёт аккорд своего макроса, а её id разрешается в имя.
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
    [NotInParallel]
    public async Task MacrosChanged_RebuildsTheBindingSet()
    {
        var dir = CreateTempDir();
        try
        {
            Write(dir, WithTriggers("first", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));

            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            using var listener = CreateListener(store);
            await Assert.That(listener.Bindings.Values.Single()).IsEqualTo("first");

            // Перепривязка макроса — обычная правка библиотеки: отдельного конфига, который надо
            // было бы синхронизировать, нет. Файл кладёт панель, демон узнаёт наблюдателем.
            Write(dir, WithTriggers("first", new HotkeyTrigger(HotkeyModifiers.Shift, VirtualKey.F13)));
            Write(dir, WithTriggers("second", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F14)));

            var rebuilt = await WaitUntilAsync(() => listener.KeyboardBindings.Count == 2);
            await Assert.That(rebuilt).IsTrue();
            await Assert.That(listener.KeyboardBindings.Any(d => d.Key == VirtualKey.F23)).IsFalse();
            await Assert.That(listener.KeyboardBindings.Select(d => d.Key).Order().ToList())
                .IsEquivalentTo(new List<VirtualKey> { VirtualKey.F13, VirtualKey.F14 });

            // Удаление макроса уносит с собой и его хоткей.
            Delete(dir, "second");
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
            Write(dir, WithTriggers("broken", new HotkeyTrigger(HotkeyModifiers.Control, 0)));
            Write(dir, WithTriggers("fine", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F20)));

            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            using var listener = CreateListener(store);

            await Assert.That(listener.Bindings.Values.Single()).IsEqualTo("fine");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // F3: гарантия «оно в библиотеке ⇒ демон его принял» ушла вместе с SaveMacro — писать в
    // macros/ теперь может кто угодно. Значит, вооружать аккорд макроса, в графе которого есть
    // ошибка, нельзя: симптомом был бы молчащий хоткей, то есть ровно тот дефект, ради которого
    // в D4 заводили GetHotkeyFailures, только с другой стороны.
    [Test]
    public async Task ABrokenGraph_DoesNotGetItsHotkeyArmed()
    {
        var dir = CreateTempDir();
        try
        {
            Write(dir, WithTriggers("здоровый", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F20)));
            Write(dir, new MacroGraph
            {
                Name = "битый",
                Triggers = [new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F21)],
                // Стартовой ноды в графе нет — жёсткая ошибка валидации.
                StartNodeId = Ids.Of("нет-такой"),
                Nodes = [new DelayNode { Id = Ids.Of("d0"), DisplayName = "d0", Ms = 10 }],
            });

            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            using var listener = CreateListener(store);

            // В библиотеке он есть — панель обязана его показать; вооружённых аккордов у него нет.
            await Assert.That(store.All).Count().IsEqualTo(2);
            await Assert.That(listener.Bindings.Values.Order().ToList())
                .IsEquivalentTo(new List<string> { "здоровый" });
            await Assert.That(listener.KeyboardBindings.Any(d => d.Key == VirtualKey.F21)).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // ---------------------------------------------------------------- приостановка как аренда

    // Панелей протокол допускает несколько, и каждая держит приостановку всё то время, что у неё
    // на экране «Макросы». Будь это флаг, уход ПЕРВОЙ вернул бы аккорды, пока у второй открыта
    // ловушка, — и RegisterHotKey снова начал бы проглатывать ровно то сочетание, которое она
    // ловит. Поэтому счётчик держателей: аккорды возвращает последний уходящий.
    [Test]
    public async Task Suspension_IsALease_SoASecondPanelKeepsTheChordsDown()
    {
        var dir = CreateTempDir();
        try
        {
            Write(dir, WithTriggers("иммунка", new HotkeyTrigger(HotkeyModifiers.None, VirtualKey.F23)));
            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            using var listener = CreateListener(store);
            await Assert.That(listener.IsSuspended).IsFalse();

            await listener.SuspendAsync();
            await listener.SuspendAsync();
            await Assert.That(listener.IsSuspended).IsTrue();

            // Первая панель ушла — вторая всё ещё ловит аккорд.
            await listener.ResumeAsync();
            await Assert.That(listener.IsSuspended).IsTrue();

            await listener.ResumeAsync();
            await Assert.That(listener.IsSuspended).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    // Отдать аренду, которой нет, обязано быть безопасно И БЕССЛЕДНО. Сюда приходят с двух дорог —
    // по запросу ResumeHotkeys и с пути разрыва соединения, — и на каждой аккуратно закрытой
    // панели вторая срабатывает после первой. Счётчик, ушедший в минус, отдаёт этот долг ЧУЖОЙ
    // арендой: следующая пара панелей разойдётся на единицу, и уход первой вернёт аккорды, пока
    // вторая ещё ловит сочетание. Поэтому проверяется не «не упало», а поведение ПОСЛЕ долга.
    [Test]
    public async Task ResumeWithoutSuspend_LeavesNoDebtForTheNextLease()
    {
        var dir = CreateTempDir();
        try
        {
            using var store = new MacroGraphStore(dir, NullLogger<MacroGraphStore>.Instance);
            using var listener = CreateListener(store);

            await listener.ResumeAsync();
            await listener.ResumeAsync();
            await Assert.That(listener.IsSuspended).IsFalse();

            // Дальше — обычная пара панелей, как если бы никакого лишнего Resume не было.
            await listener.SuspendAsync();
            await listener.SuspendAsync();
            await Assert.That(listener.IsSuspended).IsTrue();

            await listener.ResumeAsync();
            await Assert.That(listener.IsSuspended).IsTrue();

            await listener.ResumeAsync();
            await Assert.That(listener.IsSuspended).IsFalse();
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
