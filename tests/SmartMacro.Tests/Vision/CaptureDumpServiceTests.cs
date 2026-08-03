using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.GameWindows;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Vision;

// Стадия 2B вынесла обход «Сдампить снимки» из MainWindow.axaml.cs в Core, и теперь до него
// можно дотянуться по IPC (а заодно его вообще стало возможно проверять — раньше он читал строки
// view-model и звал службы со стороны Avalonia).
//
// Важна здесь договорённость об устойчивости: снимки дампят как раз потому, что что-то уже
// сломалось, — поэтому одно окно, снимок которого не удался, не имеет права стоить вам
// остальных восьми.
public class CaptureDumpServiceTests
{
    private static readonly byte[] FakeCapture = [1, 2, 3, 4];
    private static readonly byte[] FakeMask = [9, 9];

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            BaseDirectory = Path.Combine(Path.GetTempPath(), $"smartmacro-dump-{Guid.NewGuid():N}");
            Directory.CreateDirectory(BaseDirectory);
            Windows = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
            Matcher = A.Fake<IClassMatcher>();
            A.CallTo(() => Matcher.DebugBinarizeClassRegion(A<byte[]>._)).Returns(FakeMask);
            Service = new CaptureDumpService(BaseDirectory, Windows, Matcher, NullLogger<CaptureDumpService>.Instance);
        }

        public string BaseDirectory { get; }

        public WindowRegistry Windows { get; }

        public IClassMatcher Matcher { get; }

        public CaptureDumpService Service { get; }

        public IGameWindow AddWindow(nint hwnd, string process, params string[] tags)
        {
            var window = A.Fake<IGameWindow>();
            A.CallTo(() => window.Handle).Returns(hwnd);
            A.CallTo(() => window.CaptureScreenshot()).Returns(FakeCapture);
            Windows.Register(hwnd, process, window);
            foreach (var tag in tags)
            {
                Windows.AddTag(hwnd, tag);
            }
            return window;
        }

        public string[] DumpedFiles() => [.. Directory.GetFiles(Service.FolderPath).Select(Path.GetFileName).OfType<string>()];

        public void Dispose()
        {
            try
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Test]
    public async Task Dump_WritesTheCaptureAndTheMaskPerWindow()
    {
        using var fixture = new Fixture();
        fixture.AddWindow(0x10, "elementclient", "Лучник");

        var folder = await fixture.Service.DumpAsync();

        await Assert.That(folder).IsEqualTo(fixture.Service.FolderPath);
        await Assert.That(fixture.DumpedFiles()).IsEquivalentTo(new[]
        {
            "elementclient-Лучник-full.png",
            "elementclient-Лучник-class-bin.png",
        });
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(folder, "elementclient-Лучник-full.png")))
            .IsEquivalentTo(FakeCapture);
    }

    [Test]
    public async Task Dump_NamesAnUntaggedWindowByItsHandle()
    {
        using var fixture = new Fixture();
        fixture.AddWindow(0xBEEF, "elementclient");

        await fixture.Service.DumpAsync();

        // Случай без тегов здесь самый интересный: дамп обычно и снимают ПОТОМУ, что опознание
        // провалилось, — значит, имена файлов обязаны оставаться различимыми и без тегов.
        await Assert.That(fixture.DumpedFiles()).Contains("elementclient-0xBEEF-full.png");
    }

    [Test]
    public async Task Dump_SanitisesTagsThatAreNotLegalFileNames()
    {
        using var fixture = new Fixture();
        fixture.AddWindow(0x20, "elementclient", "a/b:c");

        await fixture.Service.DumpAsync();

        await Assert.That(fixture.DumpedFiles()).Contains("elementclient-a_b_c-full.png");
    }

    [Test]
    public async Task Dump_KeepsGoingWhenOneWindowFailsToCapture()
    {
        using var fixture = new Fixture();
        var broken = fixture.AddWindow(0x30, "elementclient", "битый");
        A.CallTo(() => broken.CaptureScreenshot()).Throws(new InvalidOperationException("PrintWindow вернул пусто"));
        fixture.AddWindow(0x40, "elementclient", "живой");

        await fixture.Service.DumpAsync();

        var files = fixture.DumpedFiles();
        // Отказ записан там же, где лежал бы его PNG…
        await Assert.That(files).Contains("elementclient-битый.error.txt");
        await Assert.That(files).DoesNotContain("elementclient-битый-full.png");
        // …а здоровое окно всё равно снято.
        await Assert.That(files).Contains("elementclient-живой-full.png");
        await Assert.That(files).Contains("elementclient-живой-class-bin.png");
    }

    [Test]
    public async Task Dump_RecordsABinarisationFailureWithoutLosingTheRawCapture()
    {
        using var fixture = new Fixture();
        fixture.AddWindow(0x50, "elementclient", "перс");
        A.CallTo(() => fixture.Matcher.DebugBinarizeClassRegion(A<byte[]>._))
            .Throws(new InvalidOperationException("OpenCV сломался"));

        await fixture.Service.DumpAsync();

        var files = fixture.DumpedFiles();
        await Assert.That(files).Contains("elementclient-перс-full.png");
        await Assert.That(files).Contains("elementclient-перс-class-bin.error.txt");
    }

    [Test]
    public async Task Dump_SkipsEntriesWithNoDrivableWindow_AndStillCreatesTheFolder()
    {
        using var fixture = new Fixture();
        // Зарегистрировано без фасада — снимать нечего, но и ошибкой это не считается.
        fixture.Windows.Register(0x60, "elementclient");

        var folder = await fixture.Service.DumpAsync();

        await Assert.That(Directory.Exists(folder)).IsTrue();
        await Assert.That(fixture.DumpedFiles()).IsEmpty();
    }
}
