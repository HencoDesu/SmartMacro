using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Windows;

namespace SmartMacro.Tests;

// W0.1: WindowRegistry — единственный владелец тегов окон. Покрыты жизненный цикл регистрации
// и снятия с учёта, семантика изменения тегов, нагрузки событий и изоляция снимков.
public class WindowRegistryTests
{
    private static WindowRegistry CreateRegistry() => new(NullLogger<WindowRegistry>.Instance);

    private static readonly IntPtr HwndA = new(0x1111);
    private static readonly IntPtr HwndB = new(0x2222);

    [Test]
    public async Task Register_NewWindow_ReturnsTrue_And_RaisesWindowAppeared()
    {
        var registry = CreateRegistry();
        var events = new List<ManagedWindowInfo>();
        registry.WindowAppeared += events.Add;

        var added = registry.Register(HwndA, "elementclient_64");

        await Assert.That(added).IsTrue();
        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Hwnd).IsEqualTo(HwndA);
        await Assert.That(events[0].ProcessName).IsEqualTo("elementclient_64");
        await Assert.That(events[0].Tags).IsEmpty();
    }

    [Test]
    public async Task Register_DuplicateHwnd_ReturnsFalse_And_RaisesNoSecondEvent()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        var events = 0;
        registry.WindowAppeared += _ => events++;

        var added = registry.Register(HwndA, "proc");

        await Assert.That(added).IsFalse();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task Unregister_KnownWindow_RaisesWindowClosed_WithFinalTags()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "Лучник");
        registry.AddTag(HwndA, "мастер");
        var events = new List<ManagedWindowInfo>();
        registry.WindowClosed += events.Add;

        var removed = registry.Unregister(HwndA);

        await Assert.That(removed).IsTrue();
        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Hwnd).IsEqualTo(HwndA);
        await Assert.That(events[0].Tags.Contains("Лучник")).IsTrue();
        await Assert.That(events[0].Tags.Contains("мастер")).IsTrue();
        await Assert.That(registry.GetTags(HwndA)).IsEmpty();
        await Assert.That(registry.Snapshot()).IsEmpty();
    }

    [Test]
    public async Task Unregister_UnknownWindow_ReturnsFalse_And_RaisesNoEvent()
    {
        var registry = CreateRegistry();
        var events = 0;
        registry.WindowClosed += _ => events++;

        var removed = registry.Unregister(HwndA);

        await Assert.That(removed).IsFalse();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task AddTag_RaisesTagsChanged_WithUpdatedTagSet()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        var events = new List<ManagedWindowInfo>();
        registry.WindowTagsChanged += events.Add;

        var changed = registry.AddTag(HwndA, "Жрец");

        await Assert.That(changed).IsTrue();
        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Tags.Contains("Жрец")).IsTrue();
        await Assert.That(registry.HasTag(HwndA, "Жрец")).IsTrue();
        await Assert.That(registry.GetTags(HwndA)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task AddTag_Duplicate_ReturnsFalse_And_RaisesNoEvent()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "Жрец");
        var events = 0;
        registry.WindowTagsChanged += _ => events++;

        var changed = registry.AddTag(HwndA, "Жрец");

        await Assert.That(changed).IsFalse();
        await Assert.That(events).IsEqualTo(0);
        await Assert.That(registry.GetTags(HwndA)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task AddTag_UnknownWindow_ReturnsFalse_And_RaisesNoEvent()
    {
        var registry = CreateRegistry();
        var events = 0;
        registry.WindowTagsChanged += _ => events++;

        var changed = registry.AddTag(HwndA, "Жрец");

        await Assert.That(changed).IsFalse();
        await Assert.That(events).IsEqualTo(0);
        await Assert.That(registry.HasTag(HwndA, "Жрец")).IsFalse();
    }

    [Test]
    public async Task RemoveTag_PresentTag_RaisesTagsChanged_WithShrunkSet()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "Жрец");
        registry.AddTag(HwndA, "мул");
        var events = new List<ManagedWindowInfo>();
        registry.WindowTagsChanged += events.Add;

        var changed = registry.RemoveTag(HwndA, "Жрец");

        await Assert.That(changed).IsTrue();
        await Assert.That(events).Count().IsEqualTo(1);
        await Assert.That(events[0].Tags.Contains("Жрец")).IsFalse();
        await Assert.That(events[0].Tags.Contains("мул")).IsTrue();
        await Assert.That(registry.HasTag(HwndA, "Жрец")).IsFalse();
    }

    [Test]
    public async Task RemoveTag_AbsentTag_ReturnsFalse_And_RaisesNoEvent()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        var events = 0;
        registry.WindowTagsChanged += _ => events++;

        var changed = registry.RemoveTag(HwndA, "Жрец");

        await Assert.That(changed).IsFalse();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task Tags_AreCaseSensitive()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "Мастер");

        await Assert.That(registry.HasTag(HwndA, "мастер")).IsFalse();
        await Assert.That(registry.AddTag(HwndA, "мастер")).IsTrue();
        await Assert.That(registry.GetTags(HwndA)).Count().IsEqualTo(2);
    }

    [Test]
    public async Task GetTags_ReturnsIsolatedSnapshot_UnaffectedByLaterMutations()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        registry.AddTag(HwndA, "первый");

        var snapshot = registry.GetTags(HwndA);
        registry.AddTag(HwndA, "второй");
        registry.RemoveTag(HwndA, "первый");

        await Assert.That(snapshot).Count().IsEqualTo(1);
        await Assert.That(snapshot.Contains("первый")).IsTrue();
    }

    [Test]
    public async Task Snapshot_ListsAllWindows_And_IsIsolatedFromLaterMutations()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "procA");
        registry.Register(HwndB, "procB");
        registry.AddTag(HwndA, "Лучник");

        var snapshot = registry.Snapshot();
        registry.AddTag(HwndA, "мастер");
        registry.Unregister(HwndB);

        await Assert.That(snapshot).Count().IsEqualTo(2);
        var a = snapshot.Single(w => w.Hwnd == HwndA);
        var b = snapshot.Single(w => w.Hwnd == HwndB);
        await Assert.That(a.ProcessName).IsEqualTo("procA");
        await Assert.That(a.Tags).Count().IsEqualTo(1);
        await Assert.That(b.ProcessName).IsEqualTo("procB");
        await Assert.That(registry.Snapshot()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task AddTag_IsAtomic_UnderConcurrentWriters()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");
        var changedEvents = 0;
        registry.WindowTagsChanged += _ => Interlocked.Increment(ref changedEvents);

        const int tagCount = 200;
        Parallel.For(0, tagCount, i => registry.AddTag(HwndA, $"tag-{i}"));

        await Assert.That(registry.GetTags(HwndA)).Count().IsEqualTo(tagCount);
        await Assert.That(changedEvents).IsEqualTo(tagCount);
    }

    [Test]
    public async Task AddTag_BlankTag_Throws()
    {
        var registry = CreateRegistry();
        registry.Register(HwndA, "proc");

        await Assert.That(() => registry.AddTag(HwndA, " ")).Throws<ArgumentException>();
        await Assert.That(() => registry.AddTag(HwndA, null!)).Throws<ArgumentNullException>();
    }
}
